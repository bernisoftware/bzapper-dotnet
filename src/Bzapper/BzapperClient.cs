using System;
using Bzapper.Internal;

namespace Bzapper;

/// <summary>
/// Cliente da API do bZapper. Crie uma instância por chave de API e reutilize-a (é thread-safe e mantém o pool de
/// conexões); descarte com <see cref="Dispose"/> ao encerrar a aplicação. Cada operação da API é um método
/// <c>XxxAsync</c> com o nome do <c>operationId</c> da spec (<c>sendText</c> → <see cref="SendTextAsync"/>).
/// </summary>
/// <example>
/// <code>
/// using var bz = new BzapperClient(Environment.GetEnvironmentVariable("BZAPPER_API_KEY")!);
/// var queued = await bz.SendTextAsync(new SendText { To = "+5511999990000", Body = "Olá!" });
/// Console.WriteLine(queued.MessageId);
/// </code>
/// </example>
public sealed partial class BzapperClient : IDisposable
{
    /// <summary>URL da API de produção, usada quando <see cref="BzapperClientOptions.BaseUrl"/> não é informado.</summary>
    public const string DefaultBaseUrl = "https://api.bzapper.com.br";

    /// <summary>Cria o cliente com as opções padrão. Não faz nenhuma chamada de rede.</summary>
    /// <param name="apiKey">Chave de API (<c>bz_live_…</c>) — painel do bZapper → Chaves de API.</param>
    /// <exception cref="ArgumentException">Chave vazia.</exception>
    public BzapperClient(string apiKey)
        : this(apiKey, new BzapperClientOptions())
    {
    }

    /// <summary>Cria o cliente com opções. Não faz nenhuma chamada de rede.</summary>
    /// <param name="apiKey">Chave de API (<c>bz_live_…</c>) — painel do bZapper → Chaves de API.</param>
    /// <param name="options">URL base, tempo limite, novas tentativas, idioma, projeto e <c>HttpClient</c>/handler próprios.</param>
    /// <exception cref="ArgumentException">Chave vazia ou opção inválida.</exception>
    public BzapperClient(string apiKey, BzapperClientOptions options)
    {
        Http = new BzapperHttp(ValidateKey(apiKey, "Informe a chave de API do bZapper."), options ?? throw new ArgumentNullException(nameof(options)));
    }

    internal BzapperHttp Http { get; }

    /// <summary>Libera o <c>HttpClient</c> interno (um <c>HttpClient</c> injetado nas opções não é descartado).</summary>
    public void Dispose() => Http.Dispose();

    internal static string ValidateKey(string apiKey, string missing)
    {
        if (apiKey is null)
        {
            throw new ArgumentNullException(nameof(apiKey), missing);
        }

        if (apiKey.Trim().Length == 0)
        {
            throw new ArgumentException("A chave não pode ser vazia.", nameof(apiKey));
        }

        return apiKey;
    }
}
