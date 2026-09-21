using System;
using System.Net.Http;

namespace Bzapper;

/// <summary>Opções do <see cref="BzapperClient"/> (e do <see cref="PartnerClient"/>).</summary>
public sealed class BzapperClientOptions
{
    /// <summary>URL base da API, sem barra final. Padrão: <c>https://api.bzapper.com.br</c>. Dev: <c>http://localhost:8080</c>.</summary>
    public string BaseUrl { get; set; } = BzapperClient.DefaultBaseUrl;

    /// <summary>Tempo limite de CADA tentativa (não da chamada inteira). Padrão: 30 s.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Novas tentativas além da primeira em erro de rede/tempo esgotado, 429, 502, 503 e 504. Padrão: 2.
    /// <c>0</c> desliga. As novas tentativas repetem o MESMO <c>X-Request-Id</c> e a MESMA <c>Idempotency-Key</c>.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Idioma das mensagens de erro (BCP-47, ex.: <c>pt-BR</c>, <c>en</c>), enviado como <c>Accept-Language</c>.</summary>
    public string? Locale { get; set; }

    /// <summary>
    /// Projeto (<c>X-Project-Id</c>) de toda chamada. A chave de API já traz o projeto dela; use só quando a mesma
    /// credencial opera vários projetos. Sobreponha por chamada com <see cref="RequestOptions.ProjectId"/>.
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// <c>HttpClient</c> próprio (ex.: vindo do <c>IHttpClientFactory</c>). A SDK não o descarta e respeita também o
    /// <c>HttpClient.Timeout</c> dele. Tem precedência sobre <see cref="HttpMessageHandler"/>.
    /// </summary>
    public HttpClient? HttpClient { get; set; }

    /// <summary>Handler próprio (proxy, certificados, testes). A SDK não o descarta.</summary>
    public HttpMessageHandler? HttpMessageHandler { get; set; }
}
