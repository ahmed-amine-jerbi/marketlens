using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Marketlens.Api.MarketData;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Marketlens.Api.Tests.Http;

public sealed class PublicEndpointsTests(MarketlensApiFactory factory)
{
    private const string ProblemPrefix = "urn:marketlens:problem:";

    [Fact]
    public async Task Assets_are_paged_in_key_order_without_overlap()
    {
        using var client = factory.CreateClient();

        var firstPage = await GetJsonAsync(client, "/api/v1/assets?class=crypto&limit=10");
        var nextCursor = firstPage.GetProperty("nextCursor").GetString();
        var secondPage = await GetJsonAsync(client, $"/api/v1/assets?class=crypto&limit=10&cursor={nextCursor}");

        var firstKeys = Keys(firstPage);
        var secondKeys = Keys(secondPage);
        Assert.Equal(10, firstKeys.Count);
        Assert.Equal(firstKeys.Order(StringComparer.Ordinal), firstKeys);
        Assert.Empty(firstKeys.Intersect(secondKeys));
        Assert.All(firstKeys.Concat(secondKeys), key => Assert.StartsWith("crypto:", key, StringComparison.Ordinal));
        Assert.Equal("crypto", firstPage.GetProperty("items")[0].GetProperty("class").GetString());
    }

    [Fact]
    public async Task Asset_search_matches_symbol_or_name_ignoring_case()
    {
        using var client = factory.CreateClient();

        var page = await GetJsonAsync(client, "/api/v1/assets?search=bitcoin");

        Assert.Contains("crypto:BTC", Keys(page));
        Assert.Contains("crypto:BCH", Keys(page));
    }

    [Theory]
    [InlineData("/api/v1/assets?class=bonds", "class")]
    [InlineData("/api/v1/assets?limit=0", "limit")]
    [InlineData("/api/v1/assets?cursor=not-a-key", "cursor")]
    [InlineData("/api/v1/assets/BTC", "key")]
    [InlineData("/api/v1/quotes", "keys")]
    [InlineData("/api/v1/quotes?keys=fiat:EUR&in=euros", "in")]
    [InlineData("/api/v1/conversions?from=bitcoin&to=fiat:EUR", "from")]
    [InlineData("/api/v1/conversions?from=fiat:EUR&to=fiat:TND&amount=0", "amount")]
    public async Task Invalid_parameters_are_answered_with_a_problem_naming_the_parameter(string path, string parameter)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        var problem = await ReadProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(ProblemPrefix + "invalid-parameter", problem.GetProperty("type").GetString());
        Assert.Equal(parameter, problem.GetProperty("parameter").GetString());
    }

    [Fact]
    public async Task Quotes_refuse_more_than_fifty_keys()
    {
        using var client = factory.CreateClient();
        var keys = string.Join(',', Enumerable.Range(1, 51).Select(number => $"stock:S{number}"));

        var response = await client.GetAsync($"/api/v1/quotes?keys={keys}", TestContext.Current.CancellationToken);

        await ReadProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unknown_assets_are_answered_with_not_found()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/assets/crypto:NOTACOIN", TestContext.Current.CancellationToken);

        var problem = await ReadProblemAsync(response, HttpStatusCode.NotFound);
        Assert.Equal(ProblemPrefix + "unknown-asset", problem.GetProperty("type").GetString());
        Assert.Equal("crypto:NOTACOIN", problem.GetProperty("asset").GetString());
    }

    [Fact]
    public async Task Quotes_are_converted_into_the_display_currency_with_prices_as_strings()
    {
        await WaitForPricesAsync("fiat:EUR", "stock:AAPL");
        using var client = factory.CreateClient();

        var quotes = await GetJsonAsync(client, "/api/v1/quotes?keys=fiat:EUR,stock:AAPL&in=fiat:EUR");

        var euro = quotes.EnumerateArray().Single(quote => quote.GetProperty("key").GetString() == "fiat:EUR");
        var apple = quotes.EnumerateArray().Single(quote => quote.GetProperty("key").GetString() == "stock:AAPL");
        Assert.Equal(JsonValueKind.String, apple.GetProperty("price").ValueKind);
        Assert.Equal("1", euro.GetProperty("price").GetString());
        Assert.Equal("fiat:EUR", apple.GetProperty("in").GetString());
        Assert.Equal("previous-close", apple.GetProperty("changeBasis").GetString());
        Assert.Equal("sample", apple.GetProperty("source").GetString());
        Assert.Equal("daily", apple.GetProperty("freshness").GetString());
    }

    [Fact]
    public async Task Asset_detail_carries_the_quote_in_the_requested_currency()
    {
        await WaitForPricesAsync("fiat:TND", "stock:AAPL");
        using var client = factory.CreateClient();

        var detail = await GetJsonAsync(client, "/api/v1/assets/stock:AAPL?in=fiat:TND");

        Assert.Equal("Apple", detail.GetProperty("asset").GetProperty("name").GetString());
        Assert.Equal("fiat:TND", detail.GetProperty("quote").GetProperty("in").GetString());
    }

    [Fact]
    public async Task Conversion_uses_both_usd_legs_and_reports_the_slower_one()
    {
        await WaitForPricesAsync("fiat:EUR", "fiat:TND");
        using var client = factory.CreateClient();

        var conversion = await GetJsonAsync(client, "/api/v1/conversions?from=fiat:EUR&to=fiat:TND&amount=0.25");

        var rate = decimal.Parse(conversion.GetProperty("rate").GetString()!, CultureInfo.InvariantCulture);
        var convertedAmount = decimal.Parse(conversion.GetProperty("convertedAmount").GetString()!, CultureInfo.InvariantCulture);
        Assert.InRange(rate, 3.3m, 3.5m);
        Assert.Equal(decimal.Round(0.25m * rate, 18), convertedAmount);
        Assert.Equal("0.25", conversion.GetProperty("amount").GetString());
        Assert.Equal("daily", conversion.GetProperty("freshness").GetString());
        Assert.Equal(2, conversion.GetProperty("legs").GetArrayLength());
    }

    [Fact]
    public async Task Conversion_without_a_price_for_one_leg_names_the_missing_asset()
    {
        using var client = factory.CreateClient();

        // The test host has no live crypto stream, so Bitcoin has no price.
        var response = await client.GetAsync("/api/v1/conversions?from=crypto:BTC&to=fiat:EUR", TestContext.Current.CancellationToken);

        var problem = await ReadProblemAsync(response, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(ProblemPrefix + "rate-unavailable", problem.GetProperty("type").GetString());
        Assert.Equal("crypto:BTC", problem.GetProperty("asset").GetString());
    }

    [Fact]
    public async Task Rate_limit_answers_429_with_retry_after_and_ignores_a_spoofed_forwarded_ip()
    {
        await using var strictHost = factory.WithWebHostBuilder(host =>
        {
            host.UseSetting("RateLimits:PublicPerMinute", "3");
            // The test server leaves the client address empty, and the forwarded-headers middleware
            // only checks its trust list when there is one; give requests a real outside address.
            host.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new OutsideClientAddress(IPAddress.Parse("192.0.2.10"))));
        });
        using var client = strictHost.CreateClient();

        for (var request = 1; request <= 3; request++)
        {
            using var allowed = new HttpRequestMessage(HttpMethod.Get, "/api/v1/assets?limit=1");
            allowed.Headers.Add("X-Forwarded-For", $"203.0.113.{request}");
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(allowed, TestContext.Current.CancellationToken)).StatusCode);
        }

        using var spoofed = new HttpRequestMessage(HttpMethod.Get, "/api/v1/assets?limit=1");
        spoofed.Headers.Add("X-Forwarded-For", "198.51.100.77");
        var limited = await client.SendAsync(spoofed, TestContext.Current.CancellationToken);

        var problem = await ReadProblemAsync(limited, HttpStatusCode.TooManyRequests);
        Assert.Equal(ProblemPrefix + "too-many-requests", problem.GetProperty("type").GetString());
        Assert.True(limited.Headers.RetryAfter is not null);
    }

    private sealed class OutsideClientAddress(IPAddress clientAddress) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = clientAddress;
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private async Task WaitForPricesAsync(params string[] keys)
    {
        var quoteBook = factory.Services.GetRequiredService<QuoteBook>();
        Assert.True(await Eventually.IsTrueAsync(() => keys.All(key => quoteBook.TryGet(AssetKey.Parse(key), out _))));
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response, HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static List<string> Keys(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("key").GetString()!).ToList();
}
