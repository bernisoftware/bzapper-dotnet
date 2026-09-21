using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bzapper.Internal;

namespace Bzapper;

/// <summary>
/// Verificação das entregas de webhook (local, sem rede). A API assina cada entrega com
/// <c>X-Bzapper-Signature: sha256=&lt;hex&gt;</c> = HMAC-SHA256 do <b>corpo cru</b> com o segredo do webhook
/// (devolvido uma vez por <see cref="BzapperClient.CreateWebhookAsync"/>). Verifique ANTES de processar e use os bytes
/// exatamente como chegaram — nunca o JSON re-serializado.
/// </summary>
/// <example>
/// <code>
/// // ASP.NET Core (minimal API)
/// app.MapPost("/webhooks/bzapper", async (HttpRequest req) =>
/// {
///     using var ms = new MemoryStream();
///     await req.Body.CopyToAsync(ms);
///     var body = ms.ToArray();
///     if (!Webhooks.Verify(secret, body, req.Headers[Webhooks.SignatureHeader])) return Results.Unauthorized();
///     var ev = Webhooks.ConstructEvent(secret, body, req.Headers[Webhooks.SignatureHeader]);
///     // ev.Id é estável — use para idempotência (a API pode reentregar).
///     return Results.Ok();
/// });
/// </code>
/// </example>
public static class Webhooks
{
    /// <summary>Cabeçalho com a assinatura <c>sha256=&lt;hex&gt;</c>.</summary>
    public const string SignatureHeader = "X-Bzapper-Signature";

    /// <summary>Cabeçalho com o id estável do evento (use para idempotência).</summary>
    public const string EventIdHeader = "X-Bzapper-Event-Id";

    /// <summary>Cabeçalho com o tipo do evento (ex.: <c>message.received</c>).</summary>
    public const string EventTypeHeader = "X-Bzapper-Event-Type";

    private const string Prefix = "sha256=";

    /// <summary><c>true</c> se <paramref name="signature"/> é o HMAC-SHA256 do corpo cru (comparação em tempo constante).</summary>
    /// <param name="secret">Segredo do webhook.</param>
    /// <param name="rawBody">Corpo cru, como chegou (UTF-8).</param>
    /// <param name="signature">Valor do cabeçalho <c>X-Bzapper-Signature</c>.</param>
    /// <returns>Se a assinatura confere. Assinatura vazia/ausente → <c>false</c>.</returns>
    public static bool Verify(string secret, string rawBody, string? signature)
    {
        if (rawBody is null)
        {
            throw new ArgumentNullException(nameof(rawBody));
        }

        return Verify(secret, Encoding.UTF8.GetBytes(rawBody), signature);
    }

    /// <summary><c>true</c> se <paramref name="signature"/> é o HMAC-SHA256 do corpo cru (comparação em tempo constante).</summary>
    /// <param name="secret">Segredo do webhook.</param>
    /// <param name="rawBody">Bytes do corpo, exatamente como chegaram.</param>
    /// <param name="signature">Valor do cabeçalho <c>X-Bzapper-Signature</c>.</param>
    /// <returns>Se a assinatura confere. Assinatura vazia/ausente → <c>false</c>.</returns>
    public static bool Verify(string secret, byte[] rawBody, string? signature)
    {
        if (secret is null)
        {
            throw new ArgumentNullException(nameof(secret));
        }

        if (rawBody is null)
        {
            throw new ArgumentNullException(nameof(rawBody));
        }

        if (string.IsNullOrEmpty(signature))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Sign(secret, rawBody));
        var received = Encoding.UTF8.GetBytes(signature!.Trim());
        return FixedTimeEquals(expected, received);
    }

    /// <summary>A assinatura (<c>sha256=&lt;hex&gt;</c>) que a API enviaria para este corpo — útil em testes.</summary>
    /// <param name="secret">Segredo do webhook.</param>
    /// <param name="rawBody">Bytes do corpo.</param>
    /// <returns><c>sha256=</c> + HMAC-SHA256 em hexadecimal minúsculo.</returns>
    public static string Sign(string secret, byte[] rawBody)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret ?? throw new ArgumentNullException(nameof(secret))));
        var mac = hmac.ComputeHash(rawBody ?? throw new ArgumentNullException(nameof(rawBody)));
        var hex = new StringBuilder(Prefix, Prefix.Length + (mac.Length * 2));
        foreach (var b in mac)
        {
            hex.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return hex.ToString();
    }

    /// <summary>Verifica a assinatura e decodifica o envelope do evento.</summary>
    /// <param name="secret">Segredo do webhook.</param>
    /// <param name="rawBody">Corpo cru, como chegou (UTF-8).</param>
    /// <param name="signature">Valor do cabeçalho <c>X-Bzapper-Signature</c>.</param>
    /// <returns>O evento.</returns>
    /// <exception cref="WebhookSignatureException">Assinatura ausente ou inválida — NÃO processe a entrega.</exception>
    public static WebhookEvent ConstructEvent(string secret, string rawBody, string? signature)
    {
        if (rawBody is null)
        {
            throw new ArgumentNullException(nameof(rawBody));
        }

        return ConstructEvent(secret, Encoding.UTF8.GetBytes(rawBody), signature);
    }

    /// <summary>Verifica a assinatura e decodifica o envelope do evento.</summary>
    /// <param name="secret">Segredo do webhook.</param>
    /// <param name="rawBody">Bytes do corpo, exatamente como chegaram.</param>
    /// <param name="signature">Valor do cabeçalho <c>X-Bzapper-Signature</c>.</param>
    /// <returns>O evento.</returns>
    /// <exception cref="WebhookSignatureException">Assinatura ausente ou inválida — NÃO processe a entrega.</exception>
    /// <exception cref="JsonException">Corpo assinado, mas não é um objeto JSON.</exception>
    public static WebhookEvent ConstructEvent(string secret, byte[] rawBody, string? signature)
    {
        if (!Verify(secret, rawBody, signature))
        {
            throw new WebhookSignatureException();
        }

        using var document = JsonDocument.Parse(rawBody);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("O corpo do webhook não é um objeto JSON.");
        }

        var ev = document.RootElement.Deserialize<WebhookEvent>(BzapperJson.Options) ?? new WebhookEvent();
        ev.Raw = document.RootElement.Clone();
        return ev;
    }

    private static bool FixedTimeEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < a.Length; i++)
        {
            diff |= a[i] ^ b[i];
        }

        return diff == 0;
    }
}

/// <summary>Assinatura de webhook ausente ou inválida: não processe a entrega (responda 400/401).</summary>
public sealed class WebhookSignatureException : Exception
{
    /// <summary>Cria o erro.</summary>
    public WebhookSignatureException()
        : base("Assinatura do webhook bZapper ausente ou inválida (X-Bzapper-Signature).")
    {
    }
}

/// <summary>Envelope de uma entrega de webhook. Campos novos da API ficam em <see cref="BzapperObject.AdditionalFields"/>.</summary>
public sealed class WebhookEvent : BzapperObject
{
    /// <summary>Id estável do evento (<c>event_id</c>) — use para idempotência (a API pode reentregar).</summary>
    [JsonPropertyName("event_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    /// <summary>Tipo (<c>event_type</c>), ex.: <c>message.received</c>, <c>instance.disconnected</c>, <c>connect.activated</c>.</summary>
    [JsonPropertyName("event_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    /// <summary>Instante do evento (ISO 8601), como veio.</summary>
    [JsonPropertyName("timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Timestamp { get; set; }

    /// <summary>Número (instância) do evento.</summary>
    [JsonPropertyName("instance_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InstanceId { get; set; }

    /// <summary>O <c>client_reference</c> que você mandou no envio (eventos de mensagem).</summary>
    [JsonPropertyName("client_reference")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientReference { get; set; }

    /// <summary>Grupo do WhatsApp, quando o evento aconteceu num grupo.</summary>
    [JsonPropertyName("group")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WebhookGroup? Group { get; set; }

    /// <summary>Quem enviou/disparou o evento.</summary>
    [JsonPropertyName("sender")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WebhookSender? Sender { get; set; }

    /// <summary>JIDs mencionados.</summary>
    [JsonPropertyName("mentions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Mentions { get; set; }

    /// <summary>Detalhe do evento (varia por tipo).</summary>
    [JsonPropertyName("payload")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Payload { get; set; }

    /// <summary>bZapper Connect: a conexão (qual cliente do parceiro) — só em webhooks de parceiro.</summary>
    [JsonPropertyName("connection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WebhookConnection? Connection { get; set; }

    /// <summary>O envelope original, intocado.</summary>
    [JsonIgnore]
    public JsonElement Raw { get; internal set; }
}

/// <summary>Grupo do WhatsApp de um evento.</summary>
public sealed class WebhookGroup : BzapperObject
{
    /// <summary>JID do grupo (<c>…@g.us</c>).</summary>
    [JsonPropertyName("jid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Jid { get; set; }

    /// <summary>Nome do grupo.</summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
}

/// <summary>Quem enviou/disparou um evento.</summary>
public sealed class WebhookSender : BzapperObject
{
    /// <summary>JID.</summary>
    [JsonPropertyName("jid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Jid { get; set; }

    /// <summary>LID (identificador anônimo do WhatsApp).</summary>
    [JsonPropertyName("lid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Lid { get; set; }

    /// <summary>Telefone (<c>+DDIdígitos</c>) quando conhecido.</summary>
    [JsonPropertyName("phone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Phone { get; set; }

    /// <summary>Nome (push name).</summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
}

/// <summary>Conexão do bZapper Connect a que uma entrega de parceiro se refere.</summary>
public sealed class WebhookConnection : BzapperObject
{
    /// <summary>Id da conexão.</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    /// <summary>Id do cliente no sistema do parceiro.</summary>
    [JsonPropertyName("external_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalId { get; set; }

    /// <summary>Conta bZapper do cliente.</summary>
    [JsonPropertyName("account_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccountId { get; set; }

    /// <summary>Projeto da conexão.</summary>
    [JsonPropertyName("project_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProjectId { get; set; }

    /// <summary>Estado (ex.: <c>active</c>, <c>suspended</c>, <c>revoked</c>).</summary>
    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; set; }
}
