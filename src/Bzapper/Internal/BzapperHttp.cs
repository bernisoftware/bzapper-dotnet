using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Bzapper.Internal;

/// <summary>
/// Transporte HTTP: headers, novas tentativas (com o MESMO X-Request-Id/Idempotency-Key), tempo limite por
/// tentativa e conversão de erro em <see cref="BzapperException"/> (BRIEF §3–§5).
/// </summary>
internal sealed class BzapperHttp : IDisposable
{
    internal static readonly string ClientHeader = "bzapper-dotnet/" + BzapperClient.Version;
    internal static readonly HttpMethod Patch = new HttpMethod("PATCH");

    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);
    private static readonly object RandomLock = new object();
    private static readonly Random Jitter = new Random();

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _authorization;
    private readonly string _baseUrl;
    private readonly TimeSpan _timeout;
    private readonly int _maxRetries;
    private readonly string? _locale;
    private readonly string? _projectId;

    internal BzapperHttp(string apiKey, BzapperClientOptions options)
    {
        var baseUrl = (options.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new ArgumentException($"BaseUrl inválida: '{options.BaseUrl}'. Use uma URL absoluta http(s), ex.: {BzapperClient.DefaultBaseUrl}.", nameof(options));
        }

        if (options.Timeout <= TimeSpan.Zero && options.Timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Timeout precisa ser positivo.");
        }

        if (options.MaxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRetries não pode ser negativo.");
        }

        _authorization = "Bearer " + apiKey;
        _baseUrl = baseUrl;
        _timeout = options.Timeout;
        _maxRetries = options.MaxRetries;
        _locale = string.IsNullOrWhiteSpace(options.Locale) ? null : options.Locale!.Trim();
        _projectId = string.IsNullOrWhiteSpace(options.ProjectId) ? null : options.ProjectId!.Trim();

        if (options.HttpClient is not null)
        {
            _http = options.HttpClient;
            _ownsHttp = false;
        }
        else
        {
            _http = options.HttpMessageHandler is not null
                ? new HttpClient(options.HttpMessageHandler, disposeHandler: false)
                : new HttpClient(CreateDefaultHandler(), disposeHandler: true);

            // O tempo limite é controlado por tentativa (ver ExecuteAsync); o do HttpClient fica desligado.
            _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            _ownsHttp = true;
        }
    }

    /// <summary>Espera entre tentativas. Substituível nos testes (a suíte não pode dormir de verdade).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = static (delay, ct) => Task.Delay(delay, ct);

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    /// <summary>Operação sem corpo de resposta (204).</summary>
    internal async Task SendVoidAsync(HttpMethod method, string path, Query? query, RequestBody? body, RequestOptions? options, CancellationToken cancellationToken)
    {
        var response = await ExecuteAsync(method, path, query, body, options, cancellationToken).ConfigureAwait(false);
        using var _ = ParseOrNull(response);
    }

    /// <summary>Operação cuja resposta não tem schema: o JSON como veio (<c>null</c> sem corpo).</summary>
    internal async Task<JsonElement?> SendJsonAsync(HttpMethod method, string path, Query? query, RequestBody? body, RequestOptions? options, CancellationToken cancellationToken)
    {
        var response = await ExecuteAsync(method, path, query, body, options, cancellationToken).ConfigureAwait(false);
        using var document = ParseOrNull(response);
        return document?.RootElement.Clone();
    }

    /// <summary>Resposta tipada obrigatória: corpo vazio ou <c>null</c> é <c>INVALID_RESPONSE</c>.</summary>
    internal async Task<T> SendAsync<T>(HttpMethod method, string path, Query? query, RequestBody? body, RequestOptions? options, CancellationToken cancellationToken)
        where T : class
    {
        var response = await ExecuteAsync(method, path, query, body, options, cancellationToken).ConfigureAwait(false);
        return Deserialize<T>(response) ?? throw ErrorFactory.InvalidResponse(response.Status, response.RequestId, "a resposta veio sem corpo", null);
    }

    /// <summary>Resposta tipada que pode ser <c>null</c> (ex.: assinatura no plano Free).</summary>
    internal async Task<T?> SendNullableAsync<T>(HttpMethod method, string path, Query? query, RequestBody? body, RequestOptions? options, CancellationToken cancellationToken)
        where T : class
    {
        var response = await ExecuteAsync(method, path, query, body, options, cancellationToken).ConfigureAwait(false);
        return Deserialize<T>(response);
    }

    private static T? Deserialize<T>(RawResponse response)
        where T : class
    {
        using var document = ParseOrNull(response);
        if (document is null || document.RootElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        try
        {
            return document.RootElement.Deserialize<T>(BzapperJson.Options);
        }
        catch (JsonException ex)
        {
            throw ErrorFactory.InvalidResponse(response.Status, response.RequestId, "formato inesperado: " + ex.Message, ex);
        }
        catch (NotSupportedException ex)
        {
            throw ErrorFactory.InvalidResponse(response.Status, response.RequestId, "formato inesperado: " + ex.Message, ex);
        }
    }

    /// <summary>2xx sem corpo → <c>null</c>; com corpo que não é JSON → <c>INVALID_RESPONSE</c> (nunca <c>null</c> calado).</summary>
    private static JsonDocument? ParseOrNull(RawResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.Text))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(response.Text);
        }
        catch (JsonException ex)
        {
            throw ErrorFactory.InvalidResponse(response.Status, response.RequestId, "o corpo da resposta não é JSON", ex);
        }
    }

    private async Task<RawResponse> ExecuteAsync(HttpMethod method, string path, Query? query, RequestBody? body, RequestOptions? options, CancellationToken cancellationToken)
    {
        var url = _baseUrl + path + (query?.ToString() ?? string.Empty);

        // Gerados UMA vez por chamada lógica e repetidos em toda nova tentativa: é o que torna a repetição
        // segura (a API devolve a resposta original com Idempotent-Replayed: true).
        var requestId = Guid.NewGuid().ToString("N");
        string? idempotencyKey = null;
        if (IsWrite(method))
        {
            idempotencyKey = string.IsNullOrEmpty(options?.IdempotencyKey) ? Guid.NewGuid().ToString() : options!.IdempotencyKey;
        }

        var projectId = string.IsNullOrWhiteSpace(options?.ProjectId) ? _projectId : options!.ProjectId!.Trim();
        var timeout = options?.Timeout ?? _timeout;

        for (var attempt = 0; ; attempt++)
        {
            BzapperException error;
            TimeSpan? retryAfter = null;

            using (var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                attemptCts.CancelAfter(timeout);
                try
                {
                    using var request = BuildRequest(method, url, body, requestId, idempotencyKey, projectId);
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, attemptCts.Token).ConfigureAwait(false);
                    var text = await ReadBodyAsync(response.Content, attemptCts.Token).ConfigureAwait(false);
                    var status = (int)response.StatusCode;
                    var responseRequestId = HeaderValue(response.Headers, "X-Request-Id") ?? requestId;
                    if (status >= 200 && status < 300)
                    {
                        return new RawResponse(status, text, responseRequestId);
                    }

                    retryAfter = ParseRetryAfter(response.Headers);
                    error = ErrorFactory.FromResponse(status, text, response.Headers, retryAfter, responseRequestId);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    error = new NetworkException($"NETWORK_ERROR: tempo esgotado após {timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s em {method.Method} {path} (request_id {requestId})", ex, requestId);
                }
                catch (HttpRequestException ex)
                {
                    error = new NetworkException($"NETWORK_ERROR: falha de conexão em {method.Method} {path}: {ex.Message} (request_id {requestId})", ex, requestId);
                }
                catch (IOException ex)
                {
                    error = new NetworkException($"NETWORK_ERROR: falha de conexão em {method.Method} {path}: {ex.Message} (request_id {requestId})", ex, requestId);
                }
            }

            if (attempt >= _maxRetries || !IsRetryable(error))
            {
                throw error;
            }

            await Delay(BackoffDelay(attempt, retryAfter), cancellationToken).ConfigureAwait(false);
        }
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string url, RequestBody? body, string requestId, string? idempotencyKey, string? projectId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Authorization", _authorization);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Bzapper-Client", ClientHeader);
        request.Headers.TryAddWithoutValidation("User-Agent", ClientHeader);
        request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        if (_locale is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Language", _locale);
        }

        if (projectId is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Project-Id", projectId);
        }

        // Conteúdo novo a cada tentativa (um HttpContent não pode ser reenviado).
        request.Content = body?.CreateContent();
        return request;
    }

    /// <summary>Espera antes da nova tentativa <paramref name="attempt"/> (0 = a primeira nova tentativa).</summary>
    internal static TimeSpan BackoffDelay(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } fromServer)
        {
            if (fromServer < TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            return fromServer > MaxRetryAfter ? MaxRetryAfter : fromServer;
        }

        var seconds = Math.Min(8.0, 0.5 * Math.Pow(2, attempt));
        double jitter;
        lock (RandomLock)
        {
            jitter = Jitter.NextDouble() * 0.25 * seconds;
        }

        return TimeSpan.FromSeconds(seconds + jitter);
    }

    private static bool IsWrite(HttpMethod method) =>
        method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Delete || method.Method == "PATCH";

    private static bool IsRetryable(BzapperException error) =>
        error is NetworkException || error.Status == 429 || error.Status == 502 || error.Status == 503 || error.Status == 504;

    private static Task<string> ReadBodyAsync(HttpContent? content, CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return Task.FromResult(string.Empty);
        }
#if NET5_0_OR_GREATER
        return content.ReadAsStringAsync(cancellationToken);
#else
        return content.ReadAsStringAsync();
#endif
    }

    private static HttpMessageHandler CreateDefaultHandler()
    {
#if NET5_0_OR_GREATER
        // Recicla conexões para acompanhar mudanças de DNS num cliente de vida longa.
        return new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
#else
        return new HttpClientHandler();
#endif
    }

    internal static string? HeaderValue(HttpResponseHeaders headers, string name)
    {
        if (headers.TryGetValues(name, out var values))
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return null;
    }

    /// <summary><c>Retry-After</c> em segundos (inteiro ou decimal) ou data HTTP.</summary>
    internal static TimeSpan? ParseRetryAfter(HttpResponseHeaders headers)
    {
        var raw = HeaderValue(headers, "Retry-After");
        if (raw is null)
        {
            return null;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0 && !double.IsInfinity(seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            var delta = date - DateTimeOffset.UtcNow;
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        return null;
    }

    private sealed class RawResponse
    {
        internal RawResponse(int status, string text, string requestId)
        {
            Status = status;
            Text = text;
            RequestId = requestId;
        }

        internal int Status { get; }

        internal string Text { get; }

        internal string RequestId { get; }
    }
}

/// <summary>Converte uma resposta de erro no <see cref="BzapperException"/> certo (BRIEF §4).</summary>
internal static class ErrorFactory
{
    internal const string InvalidResponseCode = "INVALID_RESPONSE";

    internal static BzapperException FromResponse(int status, string text, HttpResponseHeaders headers, TimeSpan? retryAfter, string requestId)
    {
        string? code = null;
        string? bodyMessage = null;
        JsonElement? body = null;

        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                body = root.Clone();
                if (root.ValueKind == JsonValueKind.Object)
                {
                    code = StringOrNull(root, "code") ?? StringOrNull(root, "error");
                    bodyMessage = StringOrNull(root, "message");
                }
            }
            catch (JsonException)
            {
                // Corpo não-JSON (proxy, balanceador…): cai no HTTP_<status> abaixo.
            }
        }

        code ??= "HTTP_" + status.ToString(CultureInfo.InvariantCulture);
        var requiredScope = BzapperHttp.HeaderValue(headers, "X-Required-Scope");
        var exposedRetryAfter = status == 429 ? retryAfter : null;

        var message = new StringBuilder(code);
        if (bodyMessage is not null && bodyMessage != code)
        {
            message.Append(": ").Append(bodyMessage);
        }

        message.Append(" (HTTP ").Append(status.ToString(CultureInfo.InvariantCulture));
        if (requiredScope is not null)
        {
            message.Append("; escopo exigido: ").Append(requiredScope);
        }

        if (exposedRetryAfter is { } wait)
        {
            message.Append("; tente de novo em ").Append(wait.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(" s");
        }

        message.Append("; request_id ").Append(requestId).Append(')');
        var m = message.ToString();

        return status switch
        {
            401 => new AuthenticationException(code, status, m, requestId, exposedRetryAfter, requiredScope, body),
            403 => new PermissionDeniedException(code, status, m, requestId, exposedRetryAfter, requiredScope, body),
            404 => new NotFoundException(code, status, m, requestId, exposedRetryAfter, requiredScope, body),
            409 => new ConflictException(code, status, m, requestId, exposedRetryAfter, requiredScope, body),
            400 or 422 => new ValidationException(code, status, m, requestId, exposedRetryAfter, requiredScope, body),
            429 => new RateLimitException(code, status, m, requestId, exposedRetryAfter, requiredScope, body),
            >= 500 and <= 599 => new ServerException(code, status, m, requestId, exposedRetryAfter, requiredScope, body),
            _ => new BzapperException(code, status, m, requestId, exposedRetryAfter, requiredScope, body),
        };
    }

    internal static BzapperException InvalidResponse(int status, string? requestId, string reason, Exception? inner) =>
        new BzapperException(
            InvalidResponseCode,
            status,
            $"{InvalidResponseCode}: {reason} (HTTP {status.ToString(CultureInfo.InvariantCulture)}{(requestId is null ? string.Empty : "; request_id " + requestId)})",
            requestId,
            innerException: inner);

    private static string? StringOrNull(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        return null;
    }
}
