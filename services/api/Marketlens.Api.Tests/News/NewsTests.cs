using System.Net;
using System.Text.Json;

using Marketlens.Api.MarketData;
using Marketlens.Api.News;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Marketlens.Api.Tests.News;

/// <summary>
/// News against Finnhub answers recorded on 2026-11-16: the parser, the service's cache and failure
/// rules over fixtures with a test key, and the endpoints in the shared host (which has no key).
/// </summary>
public sealed class NewsTests(MarketlensApiFactory factory) : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 17, 0, 0, TimeSpan.Zero);

    private readonly ProviderFixtures fixtures = new();
    private readonly FakeTimeProvider clock = new(Now);

    public void Dispose() => fixtures.Dispose();

    [Fact]
    public void ParseNews_reads_articles_newest_first_with_https_links()
    {
        var articles = Parse("finnhub-news-general.json");

        Assert.Equal(12, articles.Count);
        Assert.All(articles, article => Assert.Equal(Uri.UriSchemeHttps, article.Url.Scheme));
        Assert.True(articles.Zip(articles.Skip(1)).All(pair => pair.First.PublishedAtUtc >= pair.Second.PublishedAtUtc));
        Assert.False(string.IsNullOrWhiteSpace(articles[0].Headline));
        Assert.False(string.IsNullOrWhiteSpace(articles[0].Source));
    }

    [Fact]
    public void ParseNews_keeps_an_article_without_a_picture_or_summary()
    {
        var articles = Parse("finnhub-news-crypto.json");

        Assert.Contains(articles, article => article.ImageUrl is null && article.Summary is null);
        Assert.Contains(articles, article => article.ImageUrl is not null);
    }

    [Fact]
    public void ParseNews_drops_unsafe_links_and_pictures_cuts_long_text_and_repeats()
    {
        var longHeadline = new string('h', FinnhubClient.MaxHeadlineLength + 40);
        using var news = JsonDocument.Parse($$"""
            [
              {"id": 1, "headline": "javascript link", "url": "javascript:alert(1)", "datetime": 1794846000, "source": "X"},
              {"id": 2, "headline": "plain http link", "url": "http://example.com/a", "datetime": 1794846000, "source": "X"},
              {"id": 3, "headline": "{{longHeadline}}", "url": "https://example.com/b", "image": "http://example.com/i.png", "datetime": 1794846000, "source": "X"},
              {"id": 3, "headline": "repeat", "url": "https://example.com/b", "datetime": 1794846000, "source": "X"},
              {"id": 4, "headline": "no time", "url": "https://example.com/c", "source": "X"}
            ]
            """);

        var article = Assert.Single(FinnhubClient.ParseNews(news.RootElement));

        Assert.Equal(3, article.Id);
        Assert.Equal(FinnhubClient.MaxHeadlineLength, article.Headline.Length);
        Assert.Null(article.ImageUrl);
    }

    [Fact]
    public async Task A_list_is_fetched_once_within_the_cache_period_and_again_after_it()
    {
        RespondWithRecordings();
        using var service = Service();

        await service.GetMarketNewsAsync(NewsCategory.Markets, TestContext.Current.CancellationToken);
        await service.GetMarketNewsAsync(NewsCategory.Markets, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(16));
        var refreshed = await service.GetMarketNewsAsync(NewsCategory.Markets, TestContext.Current.CancellationToken);

        Assert.Equal(2, fixtures.Requests.Count(request => request.AbsolutePath == "/api/v1/news"));
        Assert.Equal(NewsStatus.Available, refreshed.Status);
    }

    [Fact]
    public async Task All_merges_markets_and_crypto_newest_first()
    {
        RespondWithRecordings();
        using var service = Service();

        var all = await service.GetMarketNewsAsync(NewsCategory.All, TestContext.Current.CancellationToken);

        Assert.Equal(24, all.Articles.Count);
        Assert.Contains(all.Articles, article => article.Source == "Reuters");
        Assert.Contains(all.Articles, article => article.Source is "Cointelegraph" or "CoinDesk");
        Assert.True(all.Articles.Zip(all.Articles.Skip(1)).All(pair => pair.First.PublishedAtUtc >= pair.Second.PublishedAtUtc));
    }

    [Fact]
    public async Task A_failed_refresh_keeps_serving_the_last_list()
    {
        RespondWithRecordings();
        using var service = Service();
        var first = await service.GetMarketNewsAsync(NewsCategory.Crypto, TestContext.Current.CancellationToken);
        fixtures.Respond("/api/v1/news", _ => throw new HttpRequestException("Finnhub is down", null, HttpStatusCode.BadGateway));
        clock.Advance(TimeSpan.FromMinutes(16));

        var afterFailure = await service.GetMarketNewsAsync(NewsCategory.Crypto, TestContext.Current.CancellationToken);

        Assert.Equal(NewsStatus.Available, afterFailure.Status);
        Assert.Equal(first.Articles, afterFailure.Articles);
    }

    [Fact]
    public async Task A_list_never_fetched_reads_unavailable_when_finnhub_fails()
    {
        fixtures.Respond("/api/v1/company-news", _ => throw new HttpRequestException("Finnhub is down", null, HttpStatusCode.BadGateway));
        using var service = Service();

        var result = await service.GetCompanyNewsAsync("AAPL", TestContext.Current.CancellationToken);

        Assert.Equal(NewsStatus.Unavailable, result.Status);
        Assert.Empty(result.Articles);
    }

    [Fact]
    public async Task Company_news_covers_the_last_week()
    {
        RespondWithRecordings();
        using var service = Service();

        var result = await service.GetCompanyNewsAsync("AAPL", TestContext.Current.CancellationToken);

        Assert.Equal(12, result.Articles.Count);
        var request = Assert.Single(fixtures.Requests, uri => uri.AbsolutePath == "/api/v1/company-news");
        Assert.Contains("from=2026-11-09", request.Query, StringComparison.Ordinal);
        Assert.Contains("to=2026-11-16", request.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/v1/news")]
    [InlineData("/api/v1/news?category=crypto")]
    [InlineData("/api/v1/assets/stock:AAPL/news")]
    public async Task Without_a_key_news_says_so(string path)
    {
        using var client = factory.CreateClient();

        var answer = await GetJsonAsync(client, path);

        Assert.Equal("no-provider-key", answer.GetProperty("status").GetString());
        Assert.Equal(0, answer.GetProperty("items").GetArrayLength());
    }

    [Theory]
    [InlineData("etf:SPY")]
    [InlineData("crypto:BTC")]
    public async Task Company_news_covers_stocks_only(string key)
    {
        using var client = factory.CreateClient();

        var answer = await GetJsonAsync(client, $"/api/v1/assets/{key}/news");

        Assert.Equal("not-covered", answer.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("/api/v1/news?category=sports", HttpStatusCode.BadRequest)]
    [InlineData("/api/v1/news?limit=0", HttpStatusCode.BadRequest)]
    [InlineData("/api/v1/news?limit=51", HttpStatusCode.BadRequest)]
    [InlineData("/api/v1/assets/stock:NOTASTOCK/news", HttpStatusCode.NotFound)]
    public async Task Bad_requests_are_problem_details(string path, HttpStatusCode expected)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private void RespondWithRecordings()
    {
        fixtures.Respond("/api/v1/news", uri => uri.Query.Contains("category=crypto", StringComparison.Ordinal)
            ? ProviderFixtures.Read("finnhub-news-crypto.json")
            : ProviderFixtures.Read("finnhub-news-general.json"));
        fixtures.Respond("/api/v1/company-news", _ => ProviderFixtures.Read("finnhub-company-news-aapl.json"));
    }

    private NewsService Service() => new(
        new FinnhubClient(new HttpClient(fixtures, disposeHandler: false) { BaseAddress = new Uri("https://finnhub.io/api/v1/") }),
        Options.Create(new NewsOptions()),
        Options.Create(new FinnhubOptions { ApiKey = "test-key" }),
        clock,
        NullLogger<NewsService>.Instance);

    private static IReadOnlyList<NewsArticle> Parse(string fixture)
    {
        using var news = JsonDocument.Parse(ProviderFixtures.Read(fixture));
        return FinnhubClient.ParseNews(news.RootElement);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.Clone();
    }
}
