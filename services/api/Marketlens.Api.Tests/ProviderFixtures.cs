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
    };

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

    public static string Read(string fileName) => File.ReadAllText(Path.Combine(FixtureDirectory, fileName), Encoding.UTF8);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Request has no URI.");
        lock (requests)
        {
            requests.Add(uri);
        }

        if (!fixtureByPath.TryGetValue(uri.AbsolutePath, out var fileName))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Read(fileName), Encoding.UTF8, "application/json"),
            RequestMessage = request,
        });
    }
}
