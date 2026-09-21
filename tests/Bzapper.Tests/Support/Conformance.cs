using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bzapper.Internal;

namespace Bzapper.Tests.Support;

/// <summary>Os casos de <c>clients/conformance/cases.json</c> (cópia em <c>fixtures/</c>).</summary>
internal static class Conformance
{
    private static readonly Lazy<JsonDocument> Document = new(() =>
        JsonDocument.Parse(File.ReadAllText(FixturePath)));

    public static string FixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "cases.json");

    public static JsonElement Root => Document.Value.RootElement;

    public static string ApiKey => Root.GetProperty("api_key").GetString()!;

    public static int MaxRetries => Root.GetProperty("max_retries").GetInt32();

    public static IEnumerable<JsonElement> Cases => Root.GetProperty("cases").EnumerateArray();

    public static HashSet<string> Ops =>
        Root.GetProperty("ops").EnumerateArray().Select(e => e.GetString()!).ToHashSet();

    public static HashSet<string> ExcludedOps =>
        Root.GetProperty("sdk_excluded_ops").EnumerateArray().Select(e => e.GetString()!).ToHashSet();

    public static JsonElement Case(string id) => Cases.Single(c => c.GetProperty("id").GetString() == id);
}

/// <summary>Raízes do repositório (funciona no monorepo e no espelho público).</summary>
internal static class Repo
{
    /// <summary>A pasta <c>clients/dotnet</c> (a que tem o <c>Bzapper.sln</c>).</summary>
    public static string DotnetRoot { get; } = FindDotnetRoot();

    private static string FindDotnetRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Bzapper.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Não achei o Bzapper.sln acima de " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// Os <c>args</c> neutros de um caso (<c>path</c>, <c>query</c>, <c>body</c>), lidos pela tabela op → chamada. Registra
/// o que foi lido: arg que a tabela não consumiu quebra o teste (parâmetro novo no caso sem suporte na SDK).
/// </summary>
internal sealed class Args
{
    private readonly JsonElement _path;
    private readonly JsonElement _query;
    private readonly JsonElement _body;
    private readonly HashSet<string> _used = new();

    public Args(JsonElement args)
    {
        _path = args.GetProperty("path");
        _query = args.GetProperty("query");
        _body = args.GetProperty("body");
    }

    public IEnumerable<string> Unused =>
        _path.EnumerateObject().Select(p => "path." + p.Name)
            .Concat(_query.EnumerateObject().Select(p => "query." + p.Name))
            .Concat(_body.ValueKind == JsonValueKind.Null ? Array.Empty<string>() : new[] { "body" })
            .Where(n => !_used.Contains(n));

    public string Path(string name)
    {
        _used.Add("path." + name);
        return _path.TryGetProperty(name, out var v) ? v.GetString()! : throw new InvalidOperationException($"path.{name} ausente no caso");
    }

    public string? QStr(string name) => Q(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;

    public int? QInt(string name) => Q(name, out var v) ? v.GetInt32() : null;

    public long? QLong(string name) => Q(name, out var v) ? v.GetInt64() : null;

    public double? QDouble(string name) => Q(name, out var v) ? v.GetDouble() : null;

    public bool? QBool(string name) => Q(name, out var v) ? v.GetBoolean() : null;

    public DateTimeOffset? QDate(string name) =>
        Q(name, out var v) ? DateTimeOffset.Parse(v.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : null;

    public IEnumerable<string>? QList(string name) =>
        Q(name, out var v) ? v.EnumerateArray().Select(e => e.GetString()!).ToList() : null;

    /// <summary>
    /// O corpo JSON no tipo da SDK. Todo campo do caso precisa existir no modelo (nada pode cair em
    /// <see cref="BzapperObject.AdditionalFields"/>); <c>null</c> no nível de cima vira <see cref="BzapperObject.ClearFields"/>.
    /// </summary>
    public T Body<T>()
        where T : BzapperObject
    {
        _used.Add("body");
        if (_body.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"o caso não tem corpo JSON para {typeof(T).Name}");
        }

        var nulls = _body.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Null).Select(p => p.Name).ToList();
        var request = _body.Deserialize<T>(BzapperJson.Options)!;
        foreach (var name in nulls)
        {
            request.ClearFields.Add(name);
        }

        var unmapped = Unmapped(request, "body").ToList();
        if (unmapped.Count > 0)
        {
            throw new InvalidOperationException($"campos do caso sem propriedade em {typeof(T).Name}: {string.Join(", ", unmapped)}");
        }

        return request;
    }

    /// <summary>O arquivo de um caso multipart (<c>body.file</c>); campos extras do form não são suportados.</summary>
    public FileUpload File()
    {
        _used.Add("body");
        var file = _body.GetProperty("file");
        if (_body.TryGetProperty("fields", out var fields) && fields.EnumerateObject().Any())
        {
            throw new InvalidOperationException("campos extras de multipart sem suporte na tabela: " + fields.GetRawText());
        }

        return FileUpload.FromBytes(
            Convert.FromBase64String(file.GetProperty("content_base64").GetString()!),
            file.GetProperty("filename").GetString()!,
            file.GetProperty("content_type").GetString());
    }

    /// <summary>Operação sem corpo: o caso também não pode ter.</summary>
    public void NoBody()
    {
        if (_body.ValueKind != JsonValueKind.Null)
        {
            throw new InvalidOperationException("o caso manda corpo para uma operação sem corpo: " + _body.GetRawText());
        }
    }

    private bool Q(string name, out JsonElement value)
    {
        _used.Add("query." + name);
        return _query.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;
    }

    private static IEnumerable<string> Unmapped(object? value, string path)
    {
        switch (value)
        {
            case BzapperObject obj:
                foreach (var key in obj.AdditionalFields?.Keys ?? Enumerable.Empty<string>())
                {
                    yield return path + "." + key;
                }

                foreach (var property in obj.GetType().GetProperties())
                {
                    if (property.GetIndexParameters().Length == 0 && property.Name is not nameof(BzapperObject.AdditionalFields) and not nameof(BzapperObject.ClearFields))
                    {
                        foreach (var inner in Unmapped(property.GetValue(obj), path + "." + property.Name))
                        {
                            yield return inner;
                        }
                    }
                }

                break;
            case System.Collections.IEnumerable list when value is not string:
                var i = 0;
                foreach (var item in list)
                {
                    foreach (var inner in Unmapped(item, $"{path}[{i++}]"))
                    {
                        yield return inner;
                    }
                }

                break;
        }
    }
}

/// <summary>
/// Confere cada troca de um caso como o BRIEF §7 manda e responde com o que o caso diz.
/// </summary>
internal sealed class ExchangeScript
{
    private static readonly Regex ClientHeader = new("^bzapper-dotnet/" + Regex.Escape(BzapperClient.Version) + "$");
    private static readonly Regex RequestId = new("^[0-9a-f]{32}$");

    private readonly JsonElement[] _exchanges;
    private readonly string _apiKey;
    private readonly string? _multipartFileName;
    private RecordedRequest? _previous;

    /// <param name="exchanges">As trocas do caso.</param>
    /// <param name="apiKey">A chave esperada no Authorization.</param>
    /// <param name="multipartFileName">Em caso <c>multipart: true</c>, o nome do arquivo que precisa aparecer no corpo.</param>
    public ExchangeScript(JsonElement[] exchanges, string apiKey, string? multipartFileName)
    {
        _exchanges = exchanges;
        _apiKey = apiKey;
        _multipartFileName = multipartFileName;
    }

    public List<string> Failures { get; } = new();

    public int Received { get; private set; }

    /// <summary>O <c>X-Request-Id</c> da última requisição recebida (o <c>"$sent"</c> dos casos).</summary>
    public string? LastRequestId => _previous?.Header("X-Request-Id");

    /// <summary>
    /// <c>"retry": true</c> = nova tentativa da troca anterior (mesmos X-Request-Id e Idempotency-Key);
    /// <c>false</c> = chamada lógica nova (ids novos).
    /// </summary>
    public static bool IsRetry(JsonElement exchange) => exchange.GetProperty("retry").GetBoolean();

    public static List<string> Segments(string path) =>
        path.Split('/').Select(Uri.UnescapeDataString).ToList();

    public MockResponse Handle(RecordedRequest actual)
    {
        var i = Received++;
        if (i >= _exchanges.Length)
        {
            Failures.Add($"requisição a mais (#{i + 1}): {actual.Method} {actual.Path}");
            return new MockResponse(500, "{}");
        }

        var tag = $"troca #{i + 1}";
        var expected = _exchanges[i].GetProperty("request");
        var method = expected.GetProperty("method").GetString()!;

        Check(actual.Method == method, $"{tag}: método {actual.Method}, esperado {method}");

        // Caminho: o CRU dividido em "/", cada segmento decodificado; o cru não pode ter espaço.
        var path = expected.GetProperty("path").GetString()!;
        Check(!actual.Path.Contains(' '), $"{tag}: caminho cru com espaço: {actual.Path}");
        Check(Segments(actual.Path).SequenceEqual(Segments(path)), $"{tag}: caminho {actual.Path}, esperado {path}");

        var expectedQuery = expected.GetProperty("query").EnumerateObject()
            .Select(p => $"{p.Name}={p.Value.GetString()}").OrderBy(s => s, StringComparer.Ordinal).ToList();
        var actualQuery = actual.Query.Select(p => $"{p.Key}={p.Value}").OrderBy(s => s, StringComparer.Ordinal).ToList();
        Check(expectedQuery.SequenceEqual(actualQuery), $"{tag}: query [{string.Join("&", actualQuery)}], esperado [{string.Join("&", expectedQuery)}]");

        var body = expected.GetProperty("body");
        var contentType = actual.Header("Content-Type") ?? string.Empty;
        if (_multipartFileName is not null)
        {
            Check(contentType.StartsWith("multipart/form-data", StringComparison.Ordinal), $"{tag}: Content-Type {contentType}, esperado multipart/form-data");
            Check(actual.Body.Contains(_multipartFileName, StringComparison.Ordinal), $"{tag}: o nome do arquivo '{_multipartFileName}' não aparece no corpo multipart");
        }
        else if (body.ValueKind == JsonValueKind.Null)
        {
            Check(actual.Body.Length == 0, $"{tag}: esperava requisição sem corpo, veio {actual.Body}");
        }
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(actual.Body);
                foreach (var diff in JsonCompare.Diff(body, doc.RootElement, lenient: false))
                {
                    Failures.Add($"{tag}: corpo {diff}");
                }
            }
            catch (JsonException)
            {
                Failures.Add($"{tag}: corpo não é JSON: '{actual.Body}'");
            }

            Check(contentType.StartsWith("application/json", StringComparison.Ordinal), $"{tag}: Content-Type {contentType}");
        }

        Check(actual.Header("Authorization") == "Bearer " + _apiKey, $"{tag}: Authorization {actual.Header("Authorization")}");
        Check(ClientHeader.IsMatch(actual.Header("X-Bzapper-Client") ?? string.Empty), $"{tag}: X-Bzapper-Client {actual.Header("X-Bzapper-Client")}");
        Check(actual.Header("User-Agent") == actual.Header("X-Bzapper-Client"), $"{tag}: User-Agent {actual.Header("User-Agent")}");
        Check(actual.Header("Accept")?.Contains("application/json") == true, $"{tag}: Accept {actual.Header("Accept")}");
        Check(RequestId.IsMatch(actual.Header("X-Request-Id") ?? string.Empty), $"{tag}: X-Request-Id {actual.Header("X-Request-Id")}");

        var isWrite = method is "POST" or "PUT" or "PATCH" or "DELETE";
        if (isWrite)
        {
            Check(!string.IsNullOrEmpty(actual.Header("Idempotency-Key")), $"{tag}: escrita sem Idempotency-Key");
        }
        else
        {
            Check(actual.Header("Idempotency-Key") is null, $"{tag}: leitura com Idempotency-Key");
        }

        if (expected.TryGetProperty("headers", out var headers))
        {
            foreach (var header in headers.EnumerateObject())
            {
                Check(actual.Header(header.Name) == header.Value.GetString(), $"{tag}: header {header.Name} = {actual.Header(header.Name)}, esperado {header.Value.GetString()}");
            }
        }

        var retry = IsRetry(_exchanges[i]);
        if (_previous is null)
        {
            Check(!retry, $"{tag}: a primeira troca de uma chamada não pode ser retry");
        }
        else if (retry)
        {
            Check(actual.Header("X-Request-Id") == _previous.Header("X-Request-Id"), $"{tag}: nova tentativa com outro X-Request-Id");
            Check(actual.Header("Idempotency-Key") == _previous.Header("Idempotency-Key"), $"{tag}: nova tentativa com outra Idempotency-Key");
        }
        else
        {
            Check(actual.Header("X-Request-Id") != _previous.Header("X-Request-Id"), $"{tag}: chamada nova reutilizou o X-Request-Id");
            if (isWrite && _previous.Header("Idempotency-Key") is not null)
            {
                Check(actual.Header("Idempotency-Key") != _previous.Header("Idempotency-Key"), $"{tag}: chamada nova reutilizou a Idempotency-Key");
            }
        }

        _previous = actual;

        var response = _exchanges[i].GetProperty("response");
        var status = response.GetProperty("status").GetInt32();
        var responseHeaders = response.GetProperty("headers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        var responseBody = response.GetProperty("body");
        return responseBody.ValueKind switch
        {
            JsonValueKind.String => new MockResponse(status, responseBody.GetString()!, responseHeaders, "text/plain; charset=utf-8"),
            JsonValueKind.Null => new MockResponse(status, string.Empty, responseHeaders),
            _ => new MockResponse(status, responseBody.GetRawText(), responseHeaders),
        };
    }

    private void Check(bool ok, string failure)
    {
        if (!ok)
        {
            Failures.Add(failure);
        }
    }
}
