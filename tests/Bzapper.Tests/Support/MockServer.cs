using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Bzapper.Tests.Support;

/// <summary>Requisição recebida pelo servidor de teste, com o caminho CRU (como veio na linha de requisição).</summary>
internal sealed record RecordedRequest(
    string Method,
    string Path,
    IReadOnlyList<KeyValuePair<string, string>> Query,
    IReadOnlyDictionary<string, string> Headers,
    string Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

    public string? Q(string name) => Query.Where(p => p.Key == name).Select(p => p.Value).FirstOrDefault();
}

internal sealed record MockResponse(int Status, string Body = "", IReadOnlyDictionary<string, string>? Headers = null, string ContentType = "application/json; charset=utf-8")
{
    public static MockResponse Json(int status, object body, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, JsonSerializer.Serialize(body), headers);

    /// <summary>200 com o objeto serializado.</summary>
    public static MockResponse Ok(object body) => Json(200, body);
}

/// <summary>Servidor HTTP local (HttpListener) que responde com um handler e grava o que recebeu.</summary>
internal sealed class MockServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<RecordedRequest> _requests = new();

    public MockServer()
    {
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public string BaseUrl { get; }

    public Func<RecordedRequest, MockResponse> Handler { get; set; } = _ => new MockResponse(500, "{}");

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    /// <summary>Uma porta TCP livre (e fechada) no loopback.</summary>
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            try
            {
                Handle(context);
            }
            catch (Exception)
            {
                try
                {
                    context.Response.Abort();
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var raw = context.Request.RawUrl ?? "/";
        var mark = raw.IndexOf('?');
        var path = mark >= 0 ? raw[..mark] : raw;
        var query = mark >= 0 ? ParseQuery(raw[(mark + 1)..]) : new List<KeyValuePair<string, string>>();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in context.Request.Headers.AllKeys)
        {
            if (key is not null)
            {
                headers[key] = context.Request.Headers[key] ?? string.Empty;
            }
        }

        string body;
        using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
        {
            body = reader.ReadToEnd();
        }

        var request = new RecordedRequest(context.Request.HttpMethod, path, query, headers, body);
        lock (_requests)
        {
            _requests.Add(request);
        }

        var response = Handler(request);
        context.Response.StatusCode = response.Status;
        if (response.Headers is not null)
        {
            foreach (var (name, value) in response.Headers)
            {
                context.Response.AddHeader(name, value);
            }
        }

        var bytes = Encoding.UTF8.GetBytes(response.Body);
        context.Response.ContentType = response.ContentType;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    private static List<KeyValuePair<string, string>> ParseQuery(string raw)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = eq >= 0 ? part[..eq] : part;
            var value = eq >= 0 ? part[(eq + 1)..] : string.Empty;
            pairs.Add(new(Decode(key), Decode(value)));
        }

        return pairs;
    }

    private static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
}
