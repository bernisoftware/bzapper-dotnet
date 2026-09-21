using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bzapper.Internal;
using Bzapper.Tests.Support;
using Xunit;

namespace Bzapper.Tests;

public class UnitTests
{
    private static readonly object Queued = new { message_id = "m-1", status = "queued" };

    // ── versão ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Version_MatchesCsproj()
    {
        var csproj = File.ReadAllText(Path.Combine(Repo.DotnetRoot, "src", "Bzapper", "Bzapper.csproj"));
        var match = Regex.Match(csproj, "<Version>([^<]+)</Version>");
        Assert.True(match.Success, "Bzapper.csproj sem <Version>");
        Assert.Equal(BzapperClient.Version, match.Groups[1].Value.Trim());

        // E o assembly testado (projeto ou pacote) foi construído com essa versão.
        var informational = typeof(BzapperClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Equal(BzapperClient.Version, informational.Split('+')[0]);
    }

    [Fact]
    public void VersionConstant_LivesInTheFileTheReleaseScriptBumps()
    {
        var source = File.ReadAllText(Path.Combine(Repo.DotnetRoot, "src", "Bzapper", "BzapperClient.Version.cs"));
        Assert.Contains($"public const string Version = \"{BzapperClient.Version}\";", source);
    }

    [Fact]
    public async Task Headers_CarryVersionAuthAndRequestId()
    {
        using var server = new MockServer { Handler = _ => MockResponse.Ok(new { status = "ok", version = "x" }) };
        using var client = Client(server);

        await client.GetHealthAsync();

        var request = server.Requests.Single();
        Assert.Equal("bzapper-dotnet/" + BzapperClient.Version, request.Header("X-Bzapper-Client"));
        Assert.Equal(request.Header("X-Bzapper-Client"), request.Header("User-Agent"));
        Assert.Equal("Bearer bz_live_x", request.Header("Authorization"));
        Assert.Matches("^[0-9a-f]{32}$", request.Header("X-Request-Id"));
        Assert.Null(request.Header("Idempotency-Key"));
        Assert.Null(request.Header("Accept-Language"));
        Assert.Null(request.Header("X-Project-Id"));
    }

    [Fact]
    public async Task LocaleAndProject_AreSent_AndProjectCanBeOverriddenPerCall()
    {
        using var server = new MockServer { Handler = _ => MockResponse.Ok(new { data = Array.Empty<object>() }) };
        using var client = new BzapperClient("bz_live_x", new BzapperClientOptions { BaseUrl = server.BaseUrl, Locale = "en", ProjectId = "p-1" });

        await client.ListTagsAsync();
        await client.ListTagsAsync(new RequestOptions { ProjectId = "p-2" });

        Assert.Equal("en", server.Requests[0].Header("Accept-Language"));
        Assert.Equal("p-1", server.Requests[0].Header("X-Project-Id"));
        Assert.Equal("p-2", server.Requests[1].Header("X-Project-Id"));
    }

    // ── rede ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NetworkError_WhenServerIsDown_CarriesTheSentRequestId()
    {
        var port = MockServer.FreePort();
        var recorder = new RecordingHandler(new HttpClientHandler());
        using var client = new BzapperClient("bz_live_x", new BzapperClientOptions
        {
            BaseUrl = $"http://127.0.0.1:{port}",
            Timeout = TimeSpan.FromSeconds(5),
            HttpMessageHandler = recorder,
        });
        var delays = NoSleep(client);

        var error = await Assert.ThrowsAsync<NetworkException>(() => client.ListInstancesAsync());

        Assert.Equal("NETWORK_ERROR", error.Code);
        Assert.Equal(0, error.Status);
        Assert.Equal(2, delays.Count);                          // MaxRetries padrão
        Assert.Equal(3, recorder.RequestIds.Count);             // 1 + 2 novas tentativas…
        Assert.Single(recorder.RequestIds.Distinct());          // …com o MESMO X-Request-Id
        Assert.Equal(recorder.RequestIds[0], error.RequestId);  // e é ele que vai no erro
        Assert.IsAssignableFrom<BzapperException>(error);
    }

    [Fact]
    public async Task Timeout_IsNetworkError()
    {
        using var server = new MockServer();
        server.Handler = _ =>
        {
            Thread.Sleep(1500);
            return MockResponse.Ok(new { });
        };
        using var client = new BzapperClient("bz_live_x", new BzapperClientOptions { BaseUrl = server.BaseUrl, Timeout = TimeSpan.FromMilliseconds(200), MaxRetries = 0 });

        var error = await Assert.ThrowsAsync<NetworkException>(() => client.GetHealthAsync());

        Assert.Equal("NETWORK_ERROR", error.Code);
        Assert.Equal(0, error.Status);
    }

    [Fact]
    public async Task CallerCancellation_IsNotANetworkError_AndIsNotRetried()
    {
        using var server = new MockServer();
        server.Handler = _ =>
        {
            Thread.Sleep(1500);
            return MockResponse.Ok(new { });
        };
        using var client = Client(server);
        var delays = NoSleep(client);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetHealthAsync(cancellationToken: cts.Token));
        Assert.Empty(delays);
    }

    // ── idempotência e novas tentativas ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UserIdempotencyKey_IsUsedVerbatim_OnEveryAttempt()
    {
        var calls = 0;
        using var server = new MockServer
        {
            Handler = _ => Interlocked.Increment(ref calls) == 1
                ? MockResponse.Json(503, new { code = "unavailable", message = "x", locale = "pt-BR" })
                : MockResponse.Json(202, Queued),
        };
        using var client = Client(server);
        NoSleep(client);

        await client.SendTextAsync(new SendText { To = "+5511999990000", Body = "oi" }, new RequestOptions { IdempotencyKey = "pedido-4471" });

        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, r => Assert.Equal("pedido-4471", r.Header("Idempotency-Key")));
        Assert.Equal(server.Requests[0].Header("X-Request-Id"), server.Requests[1].Header("X-Request-Id"));
    }

    [Fact]
    public async Task GeneratedIdempotencyKey_IsNewPerLogicalCall()
    {
        using var server = new MockServer { Handler = _ => MockResponse.Json(202, Queued) };
        using var client = Client(server);

        await client.SendTextAsync(new SendText { To = "+5511999990000", Body = "a" });
        await client.SendTextAsync(new SendText { To = "+5511999990000", Body = "b" });

        Assert.All(server.Requests, r => Assert.False(string.IsNullOrEmpty(r.Header("Idempotency-Key"))));
        Assert.NotEqual(server.Requests[0].Header("Idempotency-Key"), server.Requests[1].Header("Idempotency-Key"));
        Assert.NotEqual(server.Requests[0].Header("X-Request-Id"), server.Requests[1].Header("X-Request-Id"));
    }

    [Fact]
    public async Task RetryAfter_IsCappedAt60Seconds()
    {
        var calls = 0;
        using var server = new MockServer
        {
            Handler = _ => Interlocked.Increment(ref calls) == 1
                ? MockResponse.Json(429, new { code = "rate_limited", message = "x", locale = "pt-BR" }, new Dictionary<string, string> { ["Retry-After"] = "600" })
                : MockResponse.Ok(new { status = "ok" }),
        };
        using var client = Client(server);
        var delays = NoSleep(client);

        await client.GetHealthAsync();

        Assert.Equal(new[] { TimeSpan.FromSeconds(60) }, delays);
    }

    [Fact]
    public async Task MaxRetriesZero_DoesNotRetry()
    {
        using var server = new MockServer { Handler = _ => MockResponse.Json(503, new { code = "unavailable", message = "x", locale = "pt-BR" }) };
        using var client = new BzapperClient("bz_live_x", new BzapperClientOptions { BaseUrl = server.BaseUrl, MaxRetries = 0 });

        var error = await Assert.ThrowsAsync<ServerException>(() => client.GetHealthAsync());

        Assert.Equal("unavailable", error.Code);
        Assert.Single(server.Requests);
    }

    [Fact]
    public void Backoff_IsExponentialWithJitter_AndCappedAt8Seconds()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var baseSeconds = Math.Min(8, 0.5 * Math.Pow(2, attempt));
            var delay = BzapperHttp.BackoffDelay(attempt, null).TotalSeconds;
            Assert.InRange(delay, baseSeconds, baseSeconds * 1.25);
        }
    }

    // ── erros ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ErrorBody_FallsBackToErrorField_AndKeepsTheDecodedBody()
    {
        using var server = new MockServer { Handler = _ => MockResponse.Json(409, new { error = "not_pro", detail = new { plan = "free" } }) };
        using var client = Client(server);

        var error = await Assert.ThrowsAsync<ConflictException>(() => client.GetHealthAsync());

        Assert.Equal("not_pro", error.Code);
        Assert.Equal(409, error.Status);
        Assert.Equal("free", error.Body!.Value.GetProperty("detail").GetProperty("plan").GetString());
        Assert.Matches("^[0-9a-f]{32}$", error.RequestId);   // sem X-Request-Id na resposta → o enviado
    }

    [Fact]
    public async Task ErrorMessage_IsLocalized_ButCodeIsStable()
    {
        using var server = new MockServer { Handler = _ => MockResponse.Json(401, new { code = "unauthorized", message = "Chave inválida", locale = "pt-BR" }) };
        using var client = Client(server);

        var error = await Assert.ThrowsAsync<AuthenticationException>(() => client.GetHealthAsync());

        Assert.Equal("unauthorized", error.Code);
        Assert.Contains("Chave inválida", error.Message);
    }

    [Fact]
    public async Task EmptySuccessBody_ForTypedResult_IsInvalidResponse_NeverSilentNull()
    {
        using var server = new MockServer { Handler = _ => new MockResponse(200, string.Empty) };
        using var client = Client(server);

        var error = await Assert.ThrowsAsync<BzapperException>(() => client.GetHealthAsync());

        Assert.Equal("INVALID_RESPONSE", error.Code);
        Assert.Equal(200, error.Status);
    }

    // ── cliente ────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyApiKey_ThrowsArgumentException_NotBzapperException(string key)
    {
        var error = Assert.ThrowsAny<ArgumentException>(() => new BzapperClient(key));
        Assert.IsNotAssignableFrom<BzapperException>(error);
        Assert.ThrowsAny<ArgumentException>(() => new PartnerClient(key));
    }

    [Fact]
    public void DefaultBaseUrl_IsProduction()
    {
        Assert.Equal("https://api.bzapper.com.br", new BzapperClientOptions().BaseUrl);
        Assert.Equal(2, new BzapperClientOptions().MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(30), new BzapperClientOptions().Timeout);
    }

    [Fact]
    public void InjectedHttpClient_IsNotDisposed()
    {
        using var http = new HttpClient();
        new BzapperClient("bz_live_x", new BzapperClientOptions { HttpClient = http }).Dispose();
        Assert.Equal(TimeSpan.FromSeconds(100), http.Timeout);   // ainda utilizável e intocado
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task PathParams_EmptyOrDotSegments_AreRejectedBeforeAnyRequest(string value)
    {
        using var server = new MockServer();
        using var client = Client(server);
        using var partner = new PartnerClient("bz_partner_x", new BzapperClientOptions { BaseUrl = server.BaseUrl });

        Assert.ThrowsAny<ArgumentException>(() => { _ = client.GetInstanceAsync(value); });
        Assert.ThrowsAny<ArgumentException>(() => { _ = client.GetGroupAsync(value, "inst-1"); });
        Assert.ThrowsAny<ArgumentException>(() => { _ = client.DeleteWebhookAsync(value); });
        Assert.ThrowsAny<ArgumentException>(() => { _ = partner.GetPartnerConnectionAsync(value); });
        await Task.Yield();
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task PathSegments_ArePercentEncoded_JidsIncluded()
    {
        using var server = new MockServer { Handler = _ => new MockResponse(204) };
        using var client = Client(server);

        await client.DeleteLabelAsync("a/b c", "inst 1");
        await client.LeaveGroupAsync("120363000000000000@g.us", "inst-1");

        Assert.Equal("/labels/a%2Fb%20c", server.Requests[0].Path);
        Assert.Equal("inst 1", server.Requests[0].Q("instance_id"));
        Assert.Equal(new[] { "", "groups", "120363000000000000@g.us", "leave" }, ExchangeScript.Segments(server.Requests[1].Path));
    }

    [Fact]
    public async Task Query_BooleansLowercase_DatesUtcZ_ListsCsv_UnsetOmitted()
    {
        using var server = new MockServer { Handler = _ => MockResponse.Ok(new { data = Array.Empty<object>() }) };
        using var client = Client(server);

        await client.ListContactsAsync(
            tags: new[] { "vip", "novo" },
            hasEmail: false,
            createdAfter: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(-3)),
            limit: 50);

        var request = server.Requests.Single();
        Assert.Equal("vip,novo", request.Q("tags"));
        Assert.Equal("false", request.Q("has_email"));
        Assert.Equal("2026-09-01T03:00:00Z", request.Q("created_after"));
        Assert.Equal("50", request.Q("limit"));
        Assert.Equal(4, request.Query.Count);
    }

    // ── corpo: omitido × null ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Body_OmitsUnsetFields_AndSendsNullOnlyForClearFields()
    {
        using var server = new MockServer { Handler = _ => MockResponse.Ok(new { id = "c-1", name = "Ana" }) };
        using var client = Client(server);

        await client.UpdateContactAsync("c-1", new UpdateContact
        {
            Name = "Ana",
            ClearFields = { nameof(UpdateContact.Email), "document" },
        });

        using var body = JsonDocument.Parse(server.Requests.Single().Body);
        var fields = body.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        Assert.Equal(new[] { "document", "email", "name" }, fields.Keys.OrderBy(k => k));
        Assert.Equal(JsonValueKind.Null, fields["email"].ValueKind);
        Assert.Equal(JsonValueKind.Null, fields["document"].ValueKind);
        Assert.Equal("Ana", fields["name"].GetString());
    }

    [Fact]
    public void ClearFields_ValueWins_AndUnknownNameThrows()
    {
        var update = new UpdateContact { Name = "Ana", ClearFields = { "name" } };
        Assert.Contains("\"name\":\"Ana\"", update.ToJsonString());

        Assert.Throws<ArgumentException>(() => update.ClearFields.Add("nao_existe"));
        Assert.True(update.ClearFields.Remove(nameof(UpdateContact.Name)));
        Assert.Empty(update.ClearFields);
    }

    [Fact]
    public void AdditionalFields_AreSent_ForFieldsThisVersionDoesNotModel()
    {
        var send = new SendText { To = "+5511999990000", Body = "oi" };
        send.AdditionalFields = new Dictionary<string, JsonElement> { ["novo_campo"] = JsonSerializer.SerializeToElement(true) };

        Assert.Contains("\"novo_campo\":true", send.ToJsonString());
    }

    [Fact]
    public async Task UnknownResponseFields_ArePreserved_NotAnError()
    {
        using var server = new MockServer { Handler = _ => MockResponse.Json(202, new { message_id = "m-1", status = "queued", campo_novo = new { x = 1 } }) };
        using var client = Client(server);

        var queued = await client.SendTextAsync(new SendText { To = "+5511999990000", Body = "oi" });

        Assert.Equal("m-1", queued.MessageId);
        Assert.Equal(1, queued.AdditionalFields!["campo_novo"].GetProperty("x").GetInt32());
    }

    [Fact]
    public async Task ListResponse_IsReturnedWhole_NotUnwrapped()
    {
        using var server = new MockServer { Handler = _ => MockResponse.Ok(new { data = new[] { new { id = "i-1", status = "connected" } }, total = 1 }) };
        using var client = Client(server);

        var list = await client.ListInstancesAsync();

        Assert.Equal("i-1", list.Data!.Single().Id);
        Assert.Equal(1, list.AdditionalFields!["total"].GetInt32());
    }

    [Fact]
    public async Task Dates_AreSentInUtcWithZ()
    {
        using var server = new MockServer { Handler = _ => MockResponse.Json(202, Queued) };
        using var client = Client(server);

        await client.SendTextAsync(new SendText
        {
            To = "+5511999990000",
            Body = "lembrete",
            ScheduledAt = new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.FromHours(-3)),
        });

        Assert.Contains("\"scheduled_at\":\"2026-09-21T12:00:00Z\"", server.Requests.Single().Body);
    }

    // ── upload ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_IsMultipart_WithFileNameAndType_AndRebuiltOnRetry()
    {
        var calls = 0;
        using var server = new MockServer
        {
            Handler = _ => Interlocked.Increment(ref calls) == 1
                ? MockResponse.Json(502, new { code = "bad_gateway", message = "x", locale = "pt-BR" })
                : MockResponse.Ok(new { url = "https://cdn/x.png" }),
        };
        using var client = Client(server);
        NoSleep(client);

        var result = await client.UploadCampaignMediaAsync(FileUpload.FromBytes(Encoding.ASCII.GetBytes("PNGDATA"), "banner.png"));

        Assert.Equal("https://cdn/x.png", result.Url);
        Assert.Equal(2, server.Requests.Count);
        foreach (var request in server.Requests)
        {
            Assert.StartsWith("multipart/form-data", request.Header("Content-Type"));
            Assert.Contains("banner.png", request.Body);
            Assert.Contains("image/png", request.Body);
            Assert.Contains("PNGDATA", request.Body);
        }
    }

    // ── webhooks ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConstructEvent_ParsesTheEnvelope()
    {
        const string secret = "whsec_x";
        const string body = "{\"event_id\":\"ev-1\",\"event_type\":\"message.received\",\"instance_id\":\"i-1\",\"sender\":{\"jid\":\"5511@s.whatsapp.net\",\"phone\":\"+5511\"},\"payload\":{\"body\":\"olá\"},\"novo\":1}";
        var signature = Webhooks.Sign(secret, Encoding.UTF8.GetBytes(body));

        var ev = Webhooks.ConstructEvent(secret, body, signature);

        Assert.Equal("ev-1", ev.Id);
        Assert.Equal("message.received", ev.Type);
        Assert.Equal("+5511", ev.Sender!.Phone);
        Assert.Equal("olá", ev.Payload!.Value.GetProperty("body").GetString());
        Assert.Equal(1, ev.AdditionalFields!["novo"].GetInt32());
        Assert.Equal("ev-1", ev.Raw.GetProperty("event_id").GetString());
    }

    [Fact]
    public void Verify_RejectsMissingOrWrongSignature()
    {
        Assert.False(Webhooks.Verify("s", "{}", null));
        Assert.False(Webhooks.Verify("s", "{}", ""));
        Assert.False(Webhooks.Verify("s", "{}", "sha256=00"));
        Assert.True(Webhooks.Verify("s", "{}", Webhooks.Sign("s", Encoding.UTF8.GetBytes("{}"))));
        Assert.Throws<WebhookSignatureException>(() => Webhooks.ConstructEvent("s", "{}", "sha256=00"));
    }

    // ── Connect ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PartnerClient_UsesTheSameTransportContract()
    {
        using var server = new MockServer { Handler = _ => MockResponse.Ok(new { id = "conn-1", api_key = "bz_live_new", status = "active" }) };
        using var partner = new PartnerClient("bz_partner_x", new BzapperClientOptions { BaseUrl = server.BaseUrl });

        var connection = await partner.ExchangeConnectCodeAsync(new ExchangeConnectCodeRequest { Code = "cc_1" });

        Assert.Equal("bz_live_new", connection.ApiKey);
        Assert.Equal("conn-1", connection.Id);
        var request = server.Requests.Single();
        Assert.Equal("/partner/connect/exchange", request.Path);
        Assert.Equal("Bearer bz_partner_x", request.Header("Authorization"));
        Assert.Equal("bzapper-dotnet/" + BzapperClient.Version, request.Header("X-Bzapper-Client"));
        Assert.False(string.IsNullOrEmpty(request.Header("Idempotency-Key")));
    }

    // ── apoio ──────────────────────────────────────────────────────────────────────────────────────────

    private static BzapperClient Client(MockServer server) =>
        new BzapperClient("bz_live_x", new BzapperClientOptions { BaseUrl = server.BaseUrl });

    private static List<TimeSpan> NoSleep(BzapperClient client)
    {
        var delays = new List<TimeSpan>();
        client.Http.Delay = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };
        return delays;
    }

    /// <summary>Grava o X-Request-Id de cada tentativa antes de repassar ao handler real.</summary>
    private sealed class RecordingHandler : DelegatingHandler
    {
        public RecordingHandler(HttpMessageHandler inner)
            : base(inner)
        {
        }

        public List<string> RequestIds { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (RequestIds)
            {
                RequestIds.Add(request.Headers.GetValues("X-Request-Id").Single());
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
