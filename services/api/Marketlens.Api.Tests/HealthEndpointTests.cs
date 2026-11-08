using System.Net;

namespace Marketlens.Api.Tests;

public sealed class HealthEndpointTests(MarketlensApiFactory factory)
{
    [Fact]
    public async Task Health_reports_healthy_when_the_api_has_started()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
