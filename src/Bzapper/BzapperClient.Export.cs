using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Bzapper.Internal;

namespace Bzapper;

// Operação escrita à mão (NÃO gerada): a resposta é CSV, não JSON, e por isso ela fica fora dos casos de
// conformidade gerados (BRIEF §6, "CSV") e é coberta por teste próprio.
public sealed partial class BzapperClient
{
    /// <summary>Exporta a base de contatos em CSV — <c>GET /contacts/export</c>.</summary>
    /// <remarks>
    /// Mesmos filtros de <see cref="ListContactsAsync"/>, mas a resposta é o arquivo CSV
    /// (<c>phone,name,email,status,source,tags,groups,created_at,last_activity_at</c>; tags e grupos unidos por
    /// <c>;</c>, datas RFC 3339 em UTC), devolvido como <see cref="string"/> — a API envia em fluxo, a SDK junta o
    /// texto (reserve memória para exportações grandes e use <paramref name="limit"/> ou os filtros para fatiar).
    /// Por ser texto, esta é a ÚNICA operação que não decodifica JSON: o CSV volta exatamente como veio, sem
    /// <c>INVALID_RESPONSE</c>. Erros continuam virando <see cref="BzapperException"/> como em qualquer chamada.
    /// </remarks>
    /// <param name="search"><c>search</c> (query). Filtra por nome, telefone, e-mail ou documento.</param>
    /// <param name="tags"><c>tags</c> (query).</param>
    /// <param name="tagsMatch"><c>tags_match</c> (query). <c>any</c> (padrão) ou <c>all</c>.</param>
    /// <param name="groups"><c>groups</c> (query).</param>
    /// <param name="projectId"><c>project_id</c> (query).</param>
    /// <param name="instanceId"><c>instance_id</c> (query).</param>
    /// <param name="status"><c>status</c> (query).</param>
    /// <param name="city"><c>city</c> (query).</param>
    /// <param name="state"><c>state</c> (query).</param>
    /// <param name="country"><c>country</c> (query).</param>
    /// <param name="zip"><c>zip</c> (query).</param>
    /// <param name="document"><c>document</c> (query).</param>
    /// <param name="hasEmail"><c>has_email</c> (query).</param>
    /// <param name="lastActivityAfter"><c>last_activity_after</c> (query).</param>
    /// <param name="lastActivityBefore"><c>last_activity_before</c> (query).</param>
    /// <param name="createdAfter"><c>created_after</c> (query).</param>
    /// <param name="createdBefore"><c>created_before</c> (query).</param>
    /// <param name="sort"><c>sort</c> (query). <c>last_activity</c> (padrão), <c>name</c> ou <c>created</c>.</param>
    /// <param name="limit"><c>limit</c> (query). Teto de linhas exportadas.</param>
    /// <param name="options">Opções desta chamada (Idempotency-Key própria, tempo limite, projeto).</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    /// <returns>O CSV como texto (cabeçalho + linhas), exatamente como a API devolveu.</returns>
    /// <exception cref="BzapperException">Erro da API ou de rede (use <see cref="BzapperException.Code"/>).</exception>
    /// <example>
    /// <code>
    /// var csv = await bz.ExportContactsAsync(tags: new[] { "vip" }, hasEmail: true, limit: 5000);
    /// File.WriteAllText("contatos.csv", csv);
    /// </code>
    /// </example>
    public Task<string> ExportContactsAsync(
        string? search = null,
        IEnumerable<string>? tags = null,
        string? tagsMatch = null,
        IEnumerable<string>? groups = null,
        string? projectId = null,
        string? instanceId = null,
        string? status = null,
        string? city = null,
        string? state = null,
        string? country = null,
        string? zip = null,
        string? document = null,
        bool? hasEmail = null,
        DateTimeOffset? lastActivityAfter = null,
        DateTimeOffset? lastActivityBefore = null,
        DateTimeOffset? createdAfter = null,
        DateTimeOffset? createdBefore = null,
        string? sort = null,
        int? limit = null,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var path = "/contacts/export";
        var query = new Query()
            .Add("search", search)
            .Add("tags", tags)
            .Add("tags_match", tagsMatch)
            .Add("groups", groups)
            .Add("project_id", projectId)
            .Add("instance_id", instanceId)
            .Add("status", status)
            .Add("city", city)
            .Add("state", state)
            .Add("country", country)
            .Add("zip", zip)
            .Add("document", document)
            .Add("has_email", hasEmail)
            .Add("last_activity_after", lastActivityAfter)
            .Add("last_activity_before", lastActivityBefore)
            .Add("created_after", createdAfter)
            .Add("created_before", createdBefore)
            .Add("sort", sort)
            .Add("limit", limit);
        return Http.SendRawTextAsync(HttpMethod.Get, path, query, null, "text/csv", options, cancellationToken);
    }
}
