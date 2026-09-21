using System;
using System.Text.Json;

namespace Bzapper;

/// <summary>
/// Erro devolvido pela API do bZapper (qualquer status fora de 2xx) ou de rede. Na sua lógica, use
/// <see cref="Code"/> — é estável (ex.: <c>unauthorized</c>, <c>insufficient_scope</c>, <c>not_pro</c>); a mensagem
/// é para leitura humana, vem no idioma pedido (<see cref="BzapperClientOptions.Locale"/>) e pode mudar.
/// </summary>
public class BzapperException : Exception
{
    /// <summary>Cria o erro (útil também para simular falhas nos testes da sua aplicação).</summary>
    /// <param name="code">Código estável do erro.</param>
    /// <param name="status">Status HTTP (<c>0</c> em erro de rede).</param>
    /// <param name="message">Texto legível.</param>
    /// <param name="requestId">Id da requisição (informe ao suporte).</param>
    /// <param name="retryAfter">Espera pedida pela API (só em 429).</param>
    /// <param name="requiredScope">Escopo que faltou na chave (só em 403 de escopo).</param>
    /// <param name="body">Corpo do erro decodificado.</param>
    /// <param name="innerException">Causa original.</param>
    public BzapperException(string code, int status, string message, string? requestId = null, TimeSpan? retryAfter = null, string? requiredScope = null, JsonElement? body = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Status = status;
        RequestId = requestId;
        RetryAfter = retryAfter;
        RequiredScope = requiredScope;
        Body = body;
    }

    /// <summary>
    /// Código estável do erro: <c>code</c> do corpo, senão <c>error</c>, senão <c>HTTP_&lt;status&gt;</c> (corpo não-JSON);
    /// <c>NETWORK_ERROR</c> em falha de rede/tempo esgotado; <c>INVALID_RESPONSE</c> quando um 2xx não traz JSON.
    /// </summary>
    public string Code { get; }

    /// <summary>Status HTTP (<c>0</c> em erro de rede).</summary>
    public int Status { get; }

    /// <summary>
    /// Id da requisição — informe ao suporte. Vem do header <c>X-Request-Id</c> da resposta, senão é o
    /// <c>X-Request-Id</c> que a SDK enviou. Preenchido em toda exceção lançada pela SDK, inclusive <see cref="NetworkException"/>.
    /// </summary>
    public string? RequestId { get; }

    /// <summary>Espera pedida pela API no header <c>Retry-After</c> (só em 429).</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Escopo que faltou na chave, do header <c>X-Required-Scope</c> (só em 403 de escopo).</summary>
    public string? RequiredScope { get; }

    /// <summary>
    /// O corpo do erro decodificado (<c>{"code", "message", "locale", …}</c>) — alguns erros trazem detalhe
    /// estruturado a mais. <c>null</c> quando o corpo não era JSON (ou em erro de rede).
    /// </summary>
    public JsonElement? Body { get; }
}

/// <summary>401 — chave ausente, inválida ou revogada.</summary>
public class AuthenticationException : BzapperException
{
    /// <inheritdoc cref="BzapperException(string, int, string, string?, TimeSpan?, string?, JsonElement?, Exception?)"/>
    public AuthenticationException(string code, int status, string message, string? requestId = null, TimeSpan? retryAfter = null, string? requiredScope = null, JsonElement? body = null, Exception? innerException = null)
        : base(code, status, message, requestId, retryAfter, requiredScope, body, innerException)
    {
    }
}

/// <summary>403 — a chave não pode fazer isso (escopo faltando: veja <see cref="BzapperException.RequiredScope"/>).</summary>
public class PermissionDeniedException : BzapperException
{
    /// <inheritdoc cref="BzapperException(string, int, string, string?, TimeSpan?, string?, JsonElement?, Exception?)"/>
    public PermissionDeniedException(string code, int status, string message, string? requestId = null, TimeSpan? retryAfter = null, string? requiredScope = null, JsonElement? body = null, Exception? innerException = null)
        : base(code, status, message, requestId, retryAfter, requiredScope, body, innerException)
    {
    }
}

/// <summary>404 — recurso não encontrado.</summary>
public class NotFoundException : BzapperException
{
    /// <inheritdoc cref="BzapperException(string, int, string, string?, TimeSpan?, string?, JsonElement?, Exception?)"/>
    public NotFoundException(string code, int status, string message, string? requestId = null, TimeSpan? retryAfter = null, string? requiredScope = null, JsonElement? body = null, Exception? innerException = null)
        : base(code, status, message, requestId, retryAfter, requiredScope, body, innerException)
    {
    }
}

/// <summary>409 — conflito de estado (ex.: <c>idempotency_in_progress</c>, <c>not_pro</c>).</summary>
public class ConflictException : BzapperException
{
    /// <inheritdoc cref="BzapperException(string, int, string, string?, TimeSpan?, string?, JsonElement?, Exception?)"/>
    public ConflictException(string code, int status, string message, string? requestId = null, TimeSpan? retryAfter = null, string? requiredScope = null, JsonElement? body = null, Exception? innerException = null)
        : base(code, status, message, requestId, retryAfter, requiredScope, body, innerException)
    {
    }
}

/// <summary>400 ou 422 — corpo ou parâmetro inválido.</summary>
public class ValidationException : BzapperException
{
    /// <inheritdoc cref="BzapperException(string, int, string, string?, TimeSpan?, string?, JsonElement?, Exception?)"/>
    public ValidationException(string code, int status, string message, string? requestId = null, TimeSpan? retryAfter = null, string? requiredScope = null, JsonElement? body = null, Exception? innerException = null)
        : base(code, status, message, requestId, retryAfter, requiredScope, body, innerException)
    {
    }
}

/// <summary>429 — limite de requisições (espera sugerida em <see cref="BzapperException.RetryAfter"/>).</summary>
public class RateLimitException : BzapperException
{
    /// <inheritdoc cref="BzapperException(string, int, string, string?, TimeSpan?, string?, JsonElement?, Exception?)"/>
    public RateLimitException(string code, int status, string message, string? requestId = null, TimeSpan? retryAfter = null, string? requiredScope = null, JsonElement? body = null, Exception? innerException = null)
        : base(code, status, message, requestId, retryAfter, requiredScope, body, innerException)
    {
    }
}

/// <summary>5xx — erro do servidor (informe o <see cref="BzapperException.RequestId"/> ao suporte).</summary>
public class ServerException : BzapperException
{
    /// <inheritdoc cref="BzapperException(string, int, string, string?, TimeSpan?, string?, JsonElement?, Exception?)"/>
    public ServerException(string code, int status, string message, string? requestId = null, TimeSpan? retryAfter = null, string? requiredScope = null, JsonElement? body = null, Exception? innerException = null)
        : base(code, status, message, requestId, retryAfter, requiredScope, body, innerException)
    {
    }
}

/// <summary>Falha de conexão ou tempo esgotado (<c>Status = 0</c>, <c>Code = "NETWORK_ERROR"</c>).</summary>
public class NetworkException : BzapperException
{
    /// <summary>Código de todo erro de rede.</summary>
    public const string NetworkErrorCode = "NETWORK_ERROR";

    /// <summary>Cria o erro de rede.</summary>
    /// <param name="message">Texto legível.</param>
    /// <param name="innerException">Causa original (<c>HttpRequestException</c>, tempo esgotado…).</param>
    /// <param name="requestId">O <c>X-Request-Id</c> que a SDK enviou.</param>
    public NetworkException(string message, Exception? innerException = null, string? requestId = null)
        : base(NetworkErrorCode, 0, message, requestId, innerException: innerException)
    {
    }
}
