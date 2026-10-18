using System.Globalization;
using System.Text.Json;
using Bzapper.Internal;
using Bzapper.Tests.Support;
using Xunit;

namespace Bzapper.Tests;

/// <summary>Roda <c>clients/conformance/cases.json</c> contra um servidor HTTP local (BRIEF §7).</summary>
public class ConformanceTests
{
    private static readonly Dictionary<string, Type> ErrorTypes = new()
    {
        ["authentication"] = typeof(AuthenticationException),
        ["permission_denied"] = typeof(PermissionDeniedException),
        ["not_found"] = typeof(NotFoundException),
        ["conflict"] = typeof(ConflictException),
        ["validation"] = typeof(ValidationException),
        ["rate_limit"] = typeof(RateLimitException),
        ["server"] = typeof(ServerException),
        ["network"] = typeof(NetworkException),
        ["api"] = typeof(BzapperException),
    };

    public static IEnumerable<object[]> CaseIds() =>
        Conformance.Cases.Select(c => new object[] { c.GetProperty("id").GetString()! });

    public static IEnumerable<object[]> Signatures() =>
        Conformance.Root.GetProperty("signatures").EnumerateArray().Select(s => new object[]
        {
            s.GetProperty("id").GetString()!,
            s.GetProperty("secret").GetString()!,
            s.GetProperty("body").GetString()!,
            s.GetProperty("signature").GetString()!,
            s.GetProperty("valid").GetBoolean(),
        });

    [Theory]
    [MemberData(nameof(CaseIds))]
    public async Task Case(string id)
    {
        var @case = Conformance.Case(id);
        var op = @case.GetProperty("op").GetString()!;
        Assert.True(OpTable.Ops.TryGetValue(op, out var call), $"A op '{op}' (caso '{id}') não tem método na SDK .NET — implemente e registre em OpTable (rode tools/generate.py).");

        var exchanges = @case.GetProperty("exchanges").EnumerateArray().ToArray();
        var multipart = @case.GetProperty("multipart").GetBoolean();
        var fileName = multipart ? @case.GetProperty("args").GetProperty("body").GetProperty("file").GetProperty("filename").GetString() : null;

        using var server = new MockServer();
        var script = new ExchangeScript(exchanges, Conformance.ApiKey, fileName);
        server.Handler = script.Handle;

        var clientOptions = new BzapperClientOptions { BaseUrl = server.BaseUrl, MaxRetries = Conformance.MaxRetries };
        using var client = new BzapperClient(Conformance.ApiKey, clientOptions);
        using var partner = new PartnerClient(Conformance.ApiKey, clientOptions);
        var delays = new List<TimeSpan>();
        Func<TimeSpan, CancellationToken, Task> noSleep = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };
        client.Http.Delay = noSleep;
        partner.Http.Delay = noSleep;

        RequestOptions? options = null;
        if (@case.TryGetProperty("options", out var caseOptions) && caseOptions.TryGetProperty("idempotency_key", out var key))
        {
            options = new RequestOptions { IdempotencyKey = key.GetString() };
        }

        var args = new Args(@case.GetProperty("args"));
        object? result = null;
        Exception? error = null;
        try
        {
            result = await call!(client, partner, args, options);
        }
        catch (BzapperException e)
        {
            error = e;
        }
        catch (ArgumentException e)
        {
            error = e;
        }

        Assert.True(!args.Unused.Any(), $"args não usados pela tabela op → chamada: {string.Join(", ", args.Unused)}");
        Assert.True(script.Failures.Count == 0, "Trocas divergentes:\n" + string.Join("\n", script.Failures));
        Assert.True(script.Received == exchanges.Length, $"A chamada fez {script.Received} requisição(ões); o caso tem {exchanges.Length}.");
        CheckRetryDelays(exchanges, delays);

        var expect = @case.GetProperty("expect");
        if (expect.TryGetProperty("result", out var expected))
        {
            Assert.True(error is null, $"Esperava resultado, veio erro: {error}");
            if (expected.ValueKind == JsonValueKind.Null)
            {
                Assert.True(result is null, $"Esperava resultado null, veio {JsonSerializer.Serialize(result, result?.GetType() ?? typeof(object), BzapperJson.Options)}");
                return;
            }

            Assert.NotNull(result);
            var actual = JsonSerializer.SerializeToElement(result, result!.GetType(), BzapperJson.Options);
            var diffs = JsonCompare.Diff(expected, actual, lenient: true);
            if (diffs.Count > 0 && expected.ValueKind == JsonValueKind.Object && expected.TryGetProperty("data", out var data))
            {
                // BRIEF §7: aceita também result.data quando a SDK desembrulha a lista.
                var unwrapped = JsonCompare.Diff(data, actual, lenient: true);
                if (unwrapped.Count == 0)
                {
                    diffs = unwrapped;
                }
            }

            Assert.True(diffs.Count == 0, "Resultado diverge de expect.result:\n" + string.Join("\n", diffs));
            return;
        }

        var expectedError = expect.GetProperty("error");
        var type = expectedError.GetProperty("type").GetString()!;
        Assert.True(error is not null, $"Esperava erro {expectedError.GetRawText()}, veio resultado.");
        if (type == "argument")
        {
            Assert.IsAssignableFrom<ArgumentException>(error);
            return;
        }

        Assert.Equal(ErrorTypes[type], error!.GetType());
        var bz = (BzapperException)error;
        Assert.Equal(expectedError.GetProperty("code").GetString(), bz.Code);
        Assert.Equal(expectedError.GetProperty("status").GetInt32(), bz.Status);
        if (expectedError.TryGetProperty("request_id", out var requestId))
        {
            // "$sent" = o X-Request-Id que a SDK enviou.
            var expectedRequestId = requestId.GetString() == "$sent" ? script.LastRequestId : requestId.GetString();
            Assert.False(string.IsNullOrEmpty(expectedRequestId), "sem X-Request-Id para comparar");
            Assert.Equal(expectedRequestId, bz.RequestId);
        }

        if (expectedError.TryGetProperty("retry_after", out var retryAfter))
        {
            Assert.Equal(TimeSpan.FromSeconds(retryAfter.GetDouble()), bz.RetryAfter);
        }

        if (expectedError.TryGetProperty("required_scope", out var scope))
        {
            Assert.Equal(scope.GetString(), bz.RequiredScope);
        }

        Assert.Contains(bz.Code, bz.Message);
    }

    [Theory]
    [MemberData(nameof(Signatures))]
    public void Signature(string id, string secret, string body, string signature, bool valid)
    {
        Assert.True(valid == Webhooks.Verify(secret, body, signature), $"vetor '{id}': esperado valid={valid}");
        Assert.True(valid == Webhooks.Verify(secret, System.Text.Encoding.UTF8.GetBytes(body), signature), $"vetor '{id}' (bytes): esperado valid={valid}");
        if (valid)
        {
            Assert.Equal(signature, Webhooks.Sign(secret, System.Text.Encoding.UTF8.GetBytes(body)));
            Assert.NotNull(Webhooks.ConstructEvent(secret, body, signature));
        }
        else
        {
            Assert.Throws<WebhookSignatureException>(() => Webhooks.ConstructEvent(secret, body, signature));
        }
    }

    /// <summary>Toda op de <c>cases.json.ops</c> tem entrada na tabela — op sem método quebra a suíte.</summary>
    [Fact]
    public void EveryOpHasAnSdkCall()
    {
        var missing = Conformance.Ops.Where(op => !OpTable.Ops.ContainsKey(op)).OrderBy(op => op).ToList();
        Assert.True(missing.Count == 0, "ops sem método na SDK .NET: " + string.Join(", ", missing));
        Assert.Equal(202, Conformance.Ops.Count);
    }

    [Fact]
    public void EveryCaseOpIsInOps_AndExcludedOpsHaveNoCase()
    {
        var ops = Conformance.Ops;
        var excluded = Conformance.ExcludedOps;
        var caseOps = Conformance.Cases.Select(c => c.GetProperty("op").GetString()!).Distinct().ToList();
        var unknown = caseOps.Where(op => !ops.Contains(op)).ToList();
        var excludedWithCase = caseOps.Where(excluded.Contains).ToList();
        var withoutCase = ops.Where(op => !caseOps.Contains(op)).ToList();
        Assert.True(unknown.Count == 0, "casos com op fora de ops: " + string.Join(", ", unknown));
        Assert.True(excludedWithCase.Count == 0, "casos de ops excluídas: " + string.Join(", ", excludedWithCase));
        Assert.True(withoutCase.Count == 0, "ops sem caso: " + string.Join(", ", withoutCase));
    }

    [Fact]
    public void TableHasNoStaleOps()
    {
        var stale = OpTable.Ops.Keys.Where(op => !Conformance.Ops.Contains(op)).ToList();
        Assert.True(stale.Count == 0, "ops na tabela que sumiram de cases.json (rode tools/generate.py): " + string.Join(", ", stale));
    }

    [Fact]
    public void VendoredCasesMatchMonorepo()
    {
        // No monorepo, a cópia em fixtures/ precisa ser idêntica à fonte (no espelho público a fonte não existe).
        var source = Path.Combine(Repo.DotnetRoot, "..", "conformance", "cases.json");
        if (!File.Exists(source))
        {
            return;
        }

        var vendored = Path.Combine(Repo.DotnetRoot, "tests", "Bzapper.Tests", "fixtures", "cases.json");
        Assert.True(
            File.ReadAllText(source) == File.ReadAllText(vendored),
            "tests/Bzapper.Tests/fixtures/cases.json desatualizado — rode: python3 clients/conformance/generate.py (ele escreve as cópias de todas as SDKs)");
    }

    /// <summary>
    /// Uma espera por nova tentativa: <c>Retry-After</c> quando houver (teto 60 s); senão
    /// <c>min(8, 0.5 × 2^tentativa)</c> + até 25% de jitter. Nenhuma espera real acontece (Delay substituído).
    /// </summary>
    private static void CheckRetryDelays(JsonElement[] exchanges, List<TimeSpan> delays)
    {
        var expected = new List<(double? RetryAfter, int Attempt)>();
        var attempt = 0;
        for (var i = 1; i < exchanges.Length; i++)
        {
            if (!ExchangeScript.IsRetry(exchanges[i]))
            {
                attempt = 0;
                continue;
            }

            double? retryAfter = null;
            foreach (var header in exchanges[i - 1].GetProperty("response").GetProperty("headers").EnumerateObject())
            {
                if (header.Name.Equals("Retry-After", StringComparison.OrdinalIgnoreCase))
                {
                    retryAfter = double.Parse(header.Value.GetString()!, CultureInfo.InvariantCulture);
                }
            }

            expected.Add((retryAfter, attempt++));
        }

        Assert.Equal(expected.Count, delays.Count);
        for (var k = 0; k < expected.Count; k++)
        {
            if (expected[k].RetryAfter is { } seconds)
            {
                Assert.Equal(TimeSpan.FromSeconds(Math.Min(seconds, 60)), delays[k]);
            }
            else
            {
                var baseSeconds = Math.Min(8, 0.5 * Math.Pow(2, expected[k].Attempt));
                Assert.InRange(delays[k].TotalSeconds, baseSeconds, baseSeconds * 1.25);
            }
        }
    }
}
