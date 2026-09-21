// QuickStart da SDK .NET do bZapper.
//
//   export BZAPPER_API_KEY=bz_live_…                 # painel do bZapper → Chaves de API
//   export BZAPPER_TO=+5511999990000                 # opcional: para quem mandar o "olá"
//   export BZAPPER_BASE_URL=http://localhost:8080    # opcional (dev)
//   dotnet run --project examples/QuickStart
using Bzapper;

var apiKey = Environment.GetEnvironmentVariable("BZAPPER_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("Defina BZAPPER_API_KEY com uma chave de API do bZapper (painel → Chaves de API).");
    return 1;
}

var options = new BzapperClientOptions { Locale = "pt-BR" };
var baseUrl = Environment.GetEnvironmentVariable("BZAPPER_BASE_URL");
if (!string.IsNullOrWhiteSpace(baseUrl))
{
    options.BaseUrl = baseUrl;
}

using var bz = new BzapperClient(apiKey, options);

try
{
    // 1. Os números (instâncias) da conta e o estado de cada um.
    var instances = await bz.ListInstancesAsync();
    Console.WriteLine($"{instances.Data?.Count ?? 0} número(s):");
    foreach (var instance in instances.Data ?? new List<Instance>())
    {
        Console.WriteLine($"  - {instance.Id}: {instance.Phone ?? "(sem número)"} [{instance.Status}]");
    }

    // 2. Hello world: um texto (só se BZAPPER_TO estiver definido).
    var to = Environment.GetEnvironmentVariable("BZAPPER_TO");
    if (!string.IsNullOrWhiteSpace(to))
    {
        var queued = await bz.SendTextAsync(new SendText { To = to, Body = "Olá do bZapper (.NET)! 👋" });
        Console.WriteLine($"Mensagem {queued.MessageId} na fila ({queued.Status}).");
    }

    // 3. Uso do período.
    var usage = await bz.GetUsageAsync();
    Console.WriteLine($"Enviadas no período: {usage.Sent ?? 0}");
    return 0;
}
catch (BzapperException e)
{
    // A lógica usa e.Code (estável); informe e.RequestId ao suporte.
    Console.Error.WriteLine($"Erro {e.Code} (HTTP {e.Status}, request_id {e.RequestId ?? "-"}): {e.Message}");
    return 1;
}
