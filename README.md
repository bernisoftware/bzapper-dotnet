# bZapper — SDK oficial .NET

SDK oficial da [API do bZapper](https://api.bzapper.com.br) para .NET: mensagens de todos os tipos, números e
QR, grupos, conversas, contatos (CRM), campanhas, webhooks com verificação de assinatura e o **bZapper
Connect** — com novas tentativas idempotentes, erros tipados e modelos tipados gerados da spec. Alvos
`netstandard2.0` (.NET Framework 4.6.2+, .NET 6/7…) e `net8.0`; a única dependência é o `System.Text.Json`
(e só no `netstandard2.0`).

```bash
dotnet add package Bzapper --version 0.15.1
```

```csharp
using Bzapper;

using var bz = new BzapperClient(Environment.GetEnvironmentVariable("BZAPPER_API_KEY")!);

var msg = await bz.SendTextAsync(new SendText { To = "+5511999999999", Body = "Olá do bZapper!" });
Console.WriteLine($"{msg.MessageId} → {msg.Status}");   // "queued"
```

Cada operação da API é um método `XxxAsync` com o nome do `operationId` da spec (`sendText` →
`SendTextAsync`, `listInstances` → `ListInstancesAsync`), todos com `RequestOptions? options` e
`CancellationToken` no fim. Há um exemplo rodável em [`examples/QuickStart`](examples/QuickStart/Program.cs).

---

## Autenticação

Crie a chave de API no painel do bZapper (**Configurações → API keys**) ou por código com
`CreateMyKeyAsync`. A chave (`bz_live_...`) pertence a um **projeto** (números, inbox e estatísticas são
isolados por projeto); para agir em outro projeto da conta, informe `ProjectId` (vai no header
`X-Project-Id`). Nunca exponha a chave no navegador.

```csharp
using var bz = new BzapperClient("bz_live_...", new BzapperClientOptions
{
    Locale = "pt-BR",                    // opcional → Accept-Language (mensagens de erro traduzidas)
    Timeout = TimeSpan.FromSeconds(30),  // por tentativa (padrão 30 s)
    MaxRetries = 2,                      // novas tentativas além da primeira (padrão 2; 0 desliga)
    ProjectId = "…",                     // opcional → X-Project-Id (por chamada: RequestOptions.ProjectId)
    BaseUrl = "http://localhost:8080",   // opcional, só em dev/self-host (padrão https://api.bzapper.com.br)
    // HttpClient = httpClientFactory.CreateClient("bzapper"),   // opcional; a SDK não o descarta
});
```

Crie **um** cliente por chave e reutilize-o (é thread-safe; mantém o pool de conexões). Nenhuma chamada de
rede acontece na construção; chave vazia lança `ArgumentException` na hora.

Toda requisição envia `Authorization: Bearer <chave>`, `Accept: application/json` (a exportação em CSV pede
`text/csv`), `X-Bzapper-Client: bzapper-dotnet/<versão>` (e o mesmo `User-Agent`), um `X-Request-Id` por
chamada, `Idempotency-Key` nas escritas e `Accept-Language`/`X-Project-Id` quando configurados.

### Rotação de chave sem derrubar a integração

`RotateMyKeyAsync` cria uma chave NOVA (mesma função, escopos, projeto e nome) e mantém a antiga
funcionando por um período de graça — dá tempo de fazer o deploy sem janela de erro. A chave crua aparece
**uma única vez**, em `ApiKey`:

```csharp
var rotated = await bz.RotateMyKeyAsync(keyId, new RotateApiKey { RevokeInSeconds = 3600 });  // padrão 86400, máx. 30 dias
Console.WriteLine(rotated.ApiKey);                 // bz_live_… — guarde AGORA, não volta a ser exibida
Console.WriteLine(rotated.OldKeyExpiresAt);        // quando a antiga para de responder (null se revogada na hora)
Console.WriteLine(rotated.PreviousKey?.RotatedTo); // id da nova, gravado na antiga
```

`RevokeInSeconds = 0` revoga a antiga imediatamente. Depois do prazo ela responde `401 key_expired`; chave já
revogada ou já vencida dá `ConflictException` (`key_already_revoked` / `key_already_expired`), e a operação é
de admin (`PermissionDeniedException` com `admin_required` para os demais). As chaves de parceiro
(bZapper Connect) rotacionam por `RotatePartnerConnectionKeyAsync`.

## Modelos: omitido × `null`, campos novos

Os corpos e as respostas são classes geradas da spec (`SendText`, `Instance`, `ContactRecord`…), com
propriedades anuláveis:

- propriedade `null` (o padrão) = **não vai no corpo**;
- para **limpar** um campo (enviar `null` explícito, ex.: num PATCH), ponha o nome em `ClearFields`;
- campos que a API devolver e esta versão não conhece ficam em `AdditionalFields` (nunca é erro) — e o que
  você puser lá também é enviado.

```csharp
await bz.UpdateContactAsync(contactId, new UpdateContact
{
    Name = "Ana Souza",
    ClearFields = { nameof(UpdateContact.Email) },   // envia "email": null
});
```

Respostas `{ "data": [...] }` voltam **inteiras** (`list.Data`, mais o que vier junto). Operações que a API
responde com 204 retornam `Task`; as sem schema na spec retornam `JsonElement?`.

### Resultado tipado: `XxxTypedAsync`

Algumas operações retornavam `JsonElement?` na 0.7.0 porque a spec ainda não descrevia a resposta. Elas <!-- sem-bump -->
ganharam modelo, mas o `XxxAsync` **continua** devolvendo `JsonElement?` (a assinatura publicada não muda —
seu código compila e roda igual). O resultado tipado vem num método irmão, `XxxTypedAsync`, com os mesmos
parâmetros:

```csharp
JsonElement? raw = await bz.PauseCampaignAsync(campaignId);           // como antes (JSON sem tipo)
CampaignStatusChange st = await bz.PauseCampaignTypedAsync(campaignId);  // tipado
```

| Operação | `XxxTypedAsync` devolve |
|---|---|
| `CreateMyKey` / `ListMyKeys` | `ApiKeyCreated` / `ApiKeyList` |
| `PauseCampaign` / `ResumeCampaign` / `CancelCampaign` | `CampaignStatusChange` |
| `EditMessage` / `ForwardMessage` | `WAMessageRef` |
| `GetBlocklist` | `Blocklist` |
| `OfferCall` | `CallOffered` |
| `JoinGroup` / `GroupInviteLink` / `ListJoinRequests` | `GroupJoined` / `GroupInviteLink` / `JoinRequestList` |
| `ApplyBrand` / `UploadBrandLogo` | `BrandApplyResult` / `BrandLogoUploaded` |
| `GetAccountUsage` | `AccountUsage` |
| `UpdateProfile` | `ProfileUpdated` |

Operações novas já nascem tipadas no próprio `XxxAsync`.

## Mensagens

Todos os envios herdam de `SendBase`: `To` (obrigatório, E.164 ou JID), `InstanceId`, `PoolId`,
`QuotedMessageId`, `QuotedParticipant`, `ClientReference`, `Mentions`, `ScheduledAt`… e devolvem
`MessageQueued` (`MessageId`, `Status`, `ClientReference`, `ScheduledId`).

```csharp
// Texto
await bz.SendTextAsync(new SendText { To = "+5511999999999", Body = "Oi!" });

// Imagem / vídeo / documento / sticker — Media aceita Url OU Base64 (nunca os dois)
await bz.SendImageAsync(new SendMedia
{
    To = "+5511999999999",
    Media = new MediaInput { Url = "https://exemplo.com/foto.jpg", Caption = "Olha isso" },
});
await bz.SendDocumentAsync(new SendMedia
{
    To = "+5511999999999",
    Media = new MediaInput { Base64 = Convert.ToBase64String(pdf), Filename = "boleto.pdf", Mimetype = "application/pdf" },
});

// Áudio como nota de voz
await bz.SendAudioAsync(new SendMedia { To = "+5511999999999", Media = new MediaInput { Url = "https://…/audio.ogg", Ptt = true } });

// Localização, contato (vCard), enquete, reação
await bz.SendLocationAsync(new SendLocation { To = "+5511999999999", Latitude = -23.55, Longitude = -46.63, Name = "Escritório" });
await bz.SendContactAsync(new SendContact { To = "+5511999999999", ContactName = "Ana", ContactPhone = "+5511977776666" });
await bz.SendPollAsync(new SendPoll { To = "+5511999999999", Name = "Melhor dia?", Options = new() { "Seg", "Qua", "Sex" } });
await bz.SendReactionAsync(new SendReaction { To = "+5511999999999", QuotedMessageId = msg.MessageId, Emoji = "👍" });

// OTP: o código vai sozinho numa bolha (copiável) + texto de contexto
await bz.SendOTPAsync(new SendOTP { To = "+5511999999999", Code = "482913", ExpiryMinutes = 10 });

// Botões e lista
await bz.SendButtonsAsync(new SendButtons
{
    To = "+5511999999999",
    Body = "Confirma o pedido?",
    Buttons = new() { new Button { Id = "sim", Title = "Sim" }, new Button { Id = "nao", Title = "Não" } },
});
await bz.SendListAsync(new SendList
{
    To = "+5511999999999",
    Body = "Escolha uma opção",
    ButtonText = "Abrir menu",
    Sections = new() { new ListSection { Title = "Planos", Rows = new() { new ListRow { Id = "pro", Title = "Pro" } } } },
});
```

> **Botões e listas não são confiáveis no WhatsApp** (pior em grupos). A API sempre envia um **menu de texto
> numerado** equivalente como alternativa — o destinatário pode receber o texto numerado em vez dos botões.

```csharp
// Agendar, editar, apagar, encaminhar, marcar lido, "digitando…"
var s = await bz.SendTextAsync(new SendText { To = "+5511999999999", Body = "Lembrete", ScheduledAt = DateTimeOffset.UtcNow.AddHours(2) });
var pending = await bz.ListScheduledAsync(limit: 50);
await bz.CancelScheduledAsync(s.ScheduledId!);

await bz.EditMessageAsync(messageId, new EditMessageRequest { Text = "Texto corrigido" });
await bz.RevokeMessageAsync(messageId, forEveryone: true);
await bz.PresenceChatAsync(new PresenceRequest { InstanceId = instanceId, To = "+5511999999999", State = "typing" });
```

## Números (instâncias) e QR

```csharp
var numbers = await bz.ListInstancesAsync();
var inst = await bz.CreateInstanceAsync(new CreateInstance { Phone = "+5511999999999", Nickname = "Suporte" });

var conn = await bz.ConnectInstanceAsync(inst.Id!, method: "qr");   // ou "pair_code"
Console.WriteLine(conn.QrCode ?? conn.PairCode);                    // mostre ao usuário e aguarde "connected"

var now = await bz.GetInstanceAsync(inst.Id!);                      // Status: qr_pending → connected
await bz.SetInboundFiltersAsync(inst.Id!, new InboundFilters { IgnoreGroups = true });
await bz.DisconnectInstanceAsync(inst.Id!);
```

Os estados conhecidos estão em `InstanceStatusValues` (`Connected`, `QrPending`…) — compare com a string.

## Grupos e conversas

```csharp
var group = await bz.CreateGroupAsync(new CreateGroup { Name = "Clientes VIP", Participants = new() { "+5511988887777" } }, instanceId);
await bz.UpdateGroupParticipantsAsync(group.Jid!, new UpdateGroupParticipantsRequest { Action = "add", Participants = new() { "+5511977776666" } }, instanceId);
var invite = await bz.GroupInviteLinkTypedAsync(group.Jid!, instanceId);   // invite.InviteLink

var inbox = await bz.ListConversationsAsync(instanceId: instanceId);
var history = await bz.ConversationHistoryAsync("5511999999999@s.whatsapp.net", instanceId: instanceId, limit: 50);
```

## Contatos (CRM)

```csharp
var contact = await bz.CreateContactAsync(new CreateContact { Phone = "+5511999999999", Name = "Ana", Email = "ana@exemplo.com" });
await bz.MutateContactTagsAsync(contact.Id!, new TaxonMutation { Add = new() { "vip" } });
var vips = await bz.ListContactsAsync(tags: new[] { "vip" }, hasEmail: true, limit: 100);
await bz.OptOutContactAsync(contact.Id!);   // supressão/consentimento
var check = await bz.ContactsCheckAsync(new ContactCheckRequest { InstanceId = instanceId, Phones = new() { "+5511999999999" } });
```

O vínculo contato ↔ número/projeto é automático (a API correlaciona pelas mensagens); os filtros só leem.

### Importar em lote

`ImportContactsAsync` faz upsert de até **1000** contatos por telefone numa chamada. Linha ruim não derruba o
resto (ela vai em `Errors`); contato suprimido/opt-out/bloqueado aparece em `SkippedRows` e nunca é
ressuscitado; tags e grupos são criados sob demanda. `DryRun = true` valida tudo e **não grava nada**:

```csharp
var result = await bz.ImportContactsAsync(new ContactImport
{
    DryRun = true,
    Contacts = new()
    {
        new ContactImportRow { Phone = "+5511999999999", Name = "Ana", Email = "ana@exemplo.com", Tags = new() { "vip" } },
        new ContactImportRow { Phone = "+5511888888888", Name = "Bruno", Address = new ContactAddress { City = "São Paulo", State = "SP" } },
    },
});

Console.WriteLine($"{result.Total}: {result.Created} criados, {result.Updated} atualizados, {result.Skipped} ignorados, {result.Failed} com erro");
foreach (var row in result.Errors ?? new())
    Console.WriteLine($"linha {row.Index} ({row.Phone}): {row.Reason} {row.Detail}");
```

Lote acima do teto é `ValidationException` (`import_too_large`).

### Exportar em CSV

`ExportContactsAsync` aceita os **mesmos filtros** de `ListContactsAsync` (sem `offset`) e é a única operação
que devolve **texto**, não JSON: o CSV volta cru, como `string`, exatamente como a API mandou (cabeçalho
`phone,name,email,status,source,tags,groups,created_at,last_activity_at`; tags e grupos unidos por `;`, datas
RFC 3339 em UTC). Grave direto em disco ou jogue no seu leitor de CSV:

```csharp
var csv = await bz.ExportContactsAsync(tags: new[] { "vip" }, hasEmail: true, sort: "name", limit: 5000);
File.WriteAllText("contatos.csv", csv, Encoding.UTF8);
```

A API envia em fluxo, mas a SDK junta o texto na memória — em bases grandes, fatie com `limit` e os filtros.

## Campanhas

```csharp
var campaign = await bz.CreateCampaignAsync(new CampaignCreate
{
    Name = "Black Friday",
    PoolId = poolId,
    PacingProfile = "conservative",
    Variations = new() { new CampaignVariation { Body = "Oi {{name}}, 30% hoje!", Weight = 1 } },
});
await bz.AddCampaignRecipientsAsync(campaign.Id!, new CampaignRecipients { ContactFilter = new ContactFilter { Tags = new() { "vip" } } });
var dryRun = await bz.DryRunCampaignAsync(campaign.Id!);
await bz.StartCampaignAsync(campaign.Id!);
await bz.PauseCampaignAsync(campaign.Id!);
```

## Uploads

```csharp
await bz.UploadBrandLogoAsync(FileUpload.FromFile("logo.png"));
var media = await bz.UploadCampaignMediaAsync(FileUpload.FromBytes(bytes, "banner.jpg", "image/jpeg"));
```

## Webhooks

```csharp
var hook = await bz.CreateWebhookAsync(new CreateWebhook
{
    Url = "https://meu-app.exemplo.com/webhooks/bzapper",
    EventTypes = new() { "message.received", "instance.disconnected" },
});
// Guarde hook.Secret: é com ele que você verifica as entregas.
```

Cada entrega traz `X-Bzapper-Signature: sha256=<hex>` = HMAC-SHA256 do **corpo cru**. Verifique antes de
processar (comparação em tempo constante), usando os bytes exatamente como chegaram:

```csharp
// ASP.NET Core
app.MapPost("/webhooks/bzapper", async (HttpRequest req) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var body = ms.ToArray();

    WebhookEvent ev;
    try
    {
        ev = Webhooks.ConstructEvent(secret, body, req.Headers[Webhooks.SignatureHeader]);
    }
    catch (WebhookSignatureException)
    {
        return Results.Unauthorized();
    }

    // ev.Id é estável: use para idempotência (a API pode reentregar).
    if (ev.Type == "message.received") { /* ev.Sender, ev.Payload… */ }
    return Results.Ok();
});
```

`Webhooks.Verify(secret, body, signature)` devolve só o `bool`.

## bZapper Connect (parceiros)

Para **softwares parceiros**: o seu produto deixa os SEUS clientes assinarem o bZapper e conectarem o WhatsApp
sem sair dele, e recebe uma chave (`bz_live_...`) autorizada pelo cliente. Use o `PartnerClient` com a chave de
parceiro (`bz_partner_...`) — **só no backend**.

```csharp
using var partner = new PartnerClient(Environment.GetEnvironmentVariable("BZAPPER_PARTNER_KEY")!);

// 1. Backend cria a sessão; o front abre o componente com SessionToken.
var session = await partner.CreateConnectSessionAsync(new CreateConnectSessionRequest
{
    ExternalId = "cliente-42",   // o id do cliente no SEU sistema
    Customer = new ConnectCustomer { Name = "Ana Souza", Email = "ana@exemplo.com" },
});

// 2. O componente devolve um code de uso único; troque pela chave do cliente (mostrada uma vez).
var connection = await partner.ExchangeConnectCodeAsync(new ExchangeConnectCodeRequest { Code = code });
using var customer = new BzapperClient(connection.ApiKey!);

// 3. Gerenciar
var all = await partner.ListPartnerConnectionsAsync(externalId: "cliente-42");
await partner.RotatePartnerConnectionKeyAsync(connection.Id!);
await partner.RevokePartnerConnectionAsync(connection.Id!);
```

## Erros

Qualquer status fora de 2xx lança `BzapperException` (ou uma subclasse). **Na sua lógica, use `Code`** — é
estável; a mensagem é para humanos, vem no idioma pedido e pode mudar.

| Exceção | Quando |
| --- | --- |
| `AuthenticationException` | 401 |
| `PermissionDeniedException` | 403 (`RequiredScope` diz o escopo que faltou) |
| `NotFoundException` | 404 |
| `ConflictException` | 409 |
| `ValidationException` | 400 e 422 |
| `RateLimitException` | 429 (`RetryAfter` diz quanto esperar) |
| `ServerException` | 5xx |
| `NetworkException` | conexão/tempo esgotado (`Status = 0`, `Code = "NETWORK_ERROR"`) |
| `BzapperException` | qualquer outro status; `INVALID_RESPONSE` quando um 2xx não traz JSON |

```csharp
try
{
    await bz.SendTextAsync(new SendText { To = "+5511999999999", Body = "oi" });
}
catch (RateLimitException e)
{
    await Task.Delay(e.RetryAfter ?? TimeSpan.FromSeconds(5));
}
catch (BzapperException e) when (e.Code == "not_pro")
{
    // ofereça o upgrade
}
catch (BzapperException e)
{
    logger.LogError("bZapper {Code} (HTTP {Status}, request_id {RequestId})", e.Code, e.Status, e.RequestId);
}
```

`RequestId` está em todo erro (o `X-Request-Id` da resposta ou o que a SDK enviou) — informe-o ao suporte.
`Body` traz o corpo do erro decodificado. Parâmetro de caminho vazio, `.` ou `..` lança `ArgumentException`
antes de qualquer requisição.

## Novas tentativas e idempotência

A SDK tenta de novo (até `MaxRetries`, padrão 2) em erro de rede/tempo esgotado, 429, 502, 503 e 504 — nada
mais. Espera `Retry-After` quando a API manda (teto 60 s); senão `min(8, 0,5 × 2^tentativa)` s + até 25% de
jitter. Toda nova tentativa repete o **mesmo** `X-Request-Id` e a **mesma** `Idempotency-Key`, e a API
devolve a resposta original (`Idempotent-Replayed: true`) em vez de enviar de novo — reenviar é seguro.

Para deduplicar também reenvios feitos pela **sua** aplicação (ex.: um job que roda de novo), passe a sua chave:

```csharp
await bz.SendTextAsync(
    new SendText { To = "+5511999999999", Body = "Pedido 4471 confirmado" },
    new RequestOptions { IdempotencyKey = "pedido-4471" });   // usada como veio, válida por 24 h
```

## Fixe a versão exata

Cada release declara se muda a superfície pública. Fixe a versão exata e atualize de propósito:

```xml
<PackageReference Include="Bzapper" Version="[0.15.1]" />
```

## Desenvolvimento

```bash
dotnet test                                   # conformidade (clients/conformance/cases.json) + unitários
python3 tools/generate.py                     # regenera modelos, métodos e a tabela op → chamada da spec
python3 tools/generate.py --check             # falha se o código gerado estiver desatualizado
```

Os arquivos em `src/Bzapper/Generated/` e `tests/Bzapper.Tests/OpTable.g.cs` são gerados a partir de
`packages/sdk/openapi.yaml` (só no monorepo) — não edite à mão. A exceção é
`src/Bzapper/BzapperClient.Export.cs` (`ExportContactsAsync`): como a resposta é CSV, ela fica fora dos casos
de conformidade gerados e é escrita e testada à mão.

O gerador trava a superfície publicada em `tools/api-baseline.json` (retorno, tipos e nomes de parâmetros de
cada `XxxAsync`): mudança na spec que quebraria quem já usa a SDK aborta a geração.

## Licença

MIT — © Berni Software.
