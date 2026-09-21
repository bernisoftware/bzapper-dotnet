using System;
using Bzapper.Internal;

namespace Bzapper;

/// <summary>
/// Cliente do <b>parceiro</b> no bZapper Connect: o seu software deixa os SEUS clientes assinarem o bZapper e
/// conectarem o WhatsApp sem sair do seu produto, e recebe uma chave de API autorizada pelo cliente.
/// Autentica com a chave de parceiro (<c>Authorization: Bearer bz_partner_…</c>) — só no seu backend, nunca no
/// navegador. Com a chave do cliente (<c>bz_live_…</c>) em mãos, use o <see cref="BzapperClient"/> normal.
/// </summary>
/// <example>
/// <code>
/// using var partner = new PartnerClient(Environment.GetEnvironmentVariable("BZAPPER_PARTNER_KEY")!);
/// var session = await partner.CreateConnectSessionAsync(new CreateConnectSessionRequest
/// {
///     ExternalId = "cliente-42",
///     Customer = new ConnectCustomer { Name = "Ana Souza", Email = "ana@exemplo.com" },
/// });
/// // o front abre o componente com session.SessionToken; ele devolve um code →
/// var connection = await partner.ExchangeConnectCodeAsync(new ExchangeConnectCodeRequest { Code = code });
/// // connection.ApiKey é a chave do cliente (mostrada uma vez).
/// </code>
/// </example>
public sealed partial class PartnerClient : IDisposable
{
    /// <summary>Cria o cliente de parceiro com as opções padrão. Não faz nenhuma chamada de rede.</summary>
    /// <param name="partnerKey">Chave de parceiro (<c>bz_partner_…</c>).</param>
    /// <exception cref="ArgumentException">Chave vazia.</exception>
    public PartnerClient(string partnerKey)
        : this(partnerKey, new BzapperClientOptions())
    {
    }

    /// <summary>Cria o cliente de parceiro com opções (as mesmas do <see cref="BzapperClient"/>). Não faz nenhuma chamada de rede.</summary>
    /// <param name="partnerKey">Chave de parceiro (<c>bz_partner_…</c>).</param>
    /// <param name="options">URL base, tempo limite, novas tentativas, idioma e <c>HttpClient</c>/handler próprios.</param>
    /// <exception cref="ArgumentException">Chave vazia ou opção inválida.</exception>
    public PartnerClient(string partnerKey, BzapperClientOptions options)
    {
        Http = new BzapperHttp(BzapperClient.ValidateKey(partnerKey, "Informe a chave de parceiro do bZapper Connect."), options ?? throw new ArgumentNullException(nameof(options)));
    }

    internal BzapperHttp Http { get; }

    /// <summary>Libera o <c>HttpClient</c> interno (um <c>HttpClient</c> injetado nas opções não é descartado).</summary>
    public void Dispose() => Http.Dispose();
}
