using System;

namespace Bzapper;

/// <summary>Opções de UMA chamada.</summary>
public sealed class RequestOptions
{
    /// <summary>
    /// <c>Idempotency-Key</c> própria para escritas (POST/PUT/PATCH/DELETE), usada como veio. Sem ela, a SDK gera uma
    /// por chamada e a repete nas novas tentativas. Informe a sua (ex.: o id do pedido) para que um reenvio feito pela
    /// SUA aplicação também seja deduplicado pela API (24 h; a repetição devolve a mesma resposta). Ignorada em leituras.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>Tempo limite por tentativa só desta chamada (sobrepõe <see cref="BzapperClientOptions.Timeout"/>).</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Projeto (<c>X-Project-Id</c>) só desta chamada (sobrepõe <see cref="BzapperClientOptions.ProjectId"/>).</summary>
    public string? ProjectId { get; set; }
}
