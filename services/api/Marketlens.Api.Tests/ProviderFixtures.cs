using System.Net;
using System.Text;

namespace Marketlens.Api.Tests;

/// <summary>
/// Serves recorded provider responses (Fixtures/) by request path, so tests and the test host never
/// call a live provider. Records every request it answers.
/// </summary>
public sealed class ProviderFixtures : HttpMessageHandler
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private readonly Dictionary<string, string> fixtureByPath = new(StringComparer.Ordinal)
    {
        ["/v2/currencies"] = "frankfurter-v2-currencies.json",
        ["/v2/rates"] = "frankfurter-v2-rates-history-usd.json",
        ["/api/v3/klines"] = "binance-klines-btc-1d.json",
    };

    private readonly Dictionary<string, Func<Uri, string>> responderByPath = new(StringComparer.Ordinal);

    private readonly List<Uri> requests = [];

    public IReadOnlyList<Uri> Requests
    {
        get
        {
            lock (requests)
            {
                return requests.ToList();
            }
        }
    }

    /// <summary>Answers a path with a computed body instead of a recorded file (paging, edge cases).</summary>
    public void Respond(string path, Func<Uri, string> respond) => responderByPath[path] = respond;

    public static string Read(string fileName) => File.ReadAllText(Path.Combine(FixtureDirectory, fileName), Encoding.UTF8);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Request has no URI.");
        lock (requests)
        {
            requests.Add(uri);
        }

        string? body = null;
        if (responderByPath.TryGetValue(uri.AbsolutePath, out var respond))
        {
            body = respond(uri);
        }
        else if (fixtureByPath.TryGetValue(uri.AbsolutePath, out var fileName))
        {
            body = Read(fileName);
        }

        return Task.FromResult(body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
    }
}
