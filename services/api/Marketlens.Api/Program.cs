using System.Net.Http.Headers;

using Marketlens.Api.Conversions;
using Marketlens.Api.Data;
using Marketlens.Api.Forecasts;
using Marketlens.Api.History;
using Marketlens.Api.Http;
using Marketlens.Api.Locale;
using Marketlens.Api.MarketData;
using Marketlens.Api.Quotes;
using Marketlens.Api.Status;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var databaseUrl = builder.Configuration["DATABASE_URL"]
    ?? throw new InvalidOperationException("DATABASE_URL is not set. See .env.example for the expected Npgsql connection string.");

builder.Services.AddDbContext<MarketlensDbContext>(options => options.UseNpgsql(databaseUrl).UseSnakeCaseNamingConvention());

// Hosted services start in registration order: the schema must exist before Data Protection reads
// its key ring and before the catalogue is written.
builder.Services.AddHostedService<DatabaseMigrator>();
builder.Services.AddDataProtection().SetApplicationName("Marketlens").PersistKeysToDbContext<MarketlensDbContext>();
builder.Services.AddSingleton(AssetUniverse.Load(AssetUniverse.DefaultPath));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<QuoteBook>();
builder.Services.AddSingleton<AssetCatalog>();
builder.Services.AddHostedService(services => services.GetRequiredService<AssetCatalog>());
builder.Services.AddSingleton<QuoteSnapshots>();
builder.Services.AddHostedService(services => services.GetRequiredService<QuoteSnapshots>());
builder.Services.AddSingleton<CandleWriter>();
builder.Services.AddHostedService(services => services.GetRequiredService<CandleWriter>());

builder.Services.Configure<FrankfurterOptions>(builder.Configuration.GetSection(FrankfurterOptions.Section));
builder.Services.AddHttpClient<FrankfurterClient>((services, http) =>
    {
        http.BaseAddress = services.GetRequiredService<IOptions<FrankfurterOptions>>().Value.BaseUrl;
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Marketlens/0.1");
    })
    // The worker holds this client for the life of the process; recycling pooled connections keeps DNS changes visible.
    .UseSocketsHttpHandler((handler, _) => handler.PooledConnectionLifetime = TimeSpan.FromMinutes(15))
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
builder.Services.AddHostedService<FiatRatesWorker>();

builder.Services.Configure<BinanceOptions>(binance =>
{
    builder.Configuration.GetSection(BinanceOptions.Section).Bind(binance);
    if (builder.Configuration["BINANCE_MARKET_DATA_BASE_URL"] is { Length: > 0 } marketDataBaseUrl)
    {
        binance.MarketDataBaseUrl = new Uri(marketDataBaseUrl);
    }

    if (builder.Configuration["BINANCE_STREAM_BASE_URL"] is { Length: > 0 } streamBaseUrl)
    {
        binance.StreamBaseUrl = new Uri(streamBaseUrl);
    }
});
builder.Services.AddHttpClient<BinanceMarketDataClient>((services, http) =>
    {
        http.BaseAddress = services.GetRequiredService<IOptions<BinanceOptions>>().Value.MarketDataBaseUrl;
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Marketlens/0.1");
    })
    .UseSocketsHttpHandler((handler, _) => handler.PooledConnectionLifetime = TimeSpan.FromMinutes(15))
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
builder.Services.AddHostedService<BinanceKlinesBackfill>();
builder.Services.AddHostedService<BinanceLiveFeed>();

builder.Services.Configure<FinnhubOptions>(finnhub =>
{
    builder.Configuration.GetSection(FinnhubOptions.Section).Bind(finnhub);
    finnhub.ApiKey = builder.Configuration["FINNHUB_API_KEY"];
});
builder.Services.AddHttpClient<FinnhubClient>((services, http) =>
    {
        var finnhub = services.GetRequiredService<IOptions<FinnhubOptions>>().Value;
        http.BaseAddress = finnhub.BaseUrl;
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Marketlens/0.1");
        if (finnhub.HasApiKey)
        {
            http.DefaultRequestHeaders.Add(FinnhubClient.ApiKeyHeader, finnhub.ApiKey);
        }
    })
    .UseSocketsHttpHandler((handler, _) => handler.PooledConnectionLifetime = TimeSpan.FromMinutes(15))
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
builder.Services.AddHostedService<FinnhubLiveFeed>();
builder.Services.AddHostedService<FinnhubQuotePoller>();

builder.Services.Configure<CoinGeckoOptions>(coinGecko =>
{
    builder.Configuration.GetSection(CoinGeckoOptions.Section).Bind(coinGecko);
    coinGecko.ApiKey = builder.Configuration["COINGECKO_DEMO_API_KEY"];
});
builder.Services.AddHttpClient<CoinGeckoClient>((services, http) =>
    {
        var coinGecko = services.GetRequiredService<IOptions<CoinGeckoOptions>>().Value;
        http.BaseAddress = coinGecko.BaseUrl;
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Marketlens/0.1");
        if (coinGecko.HasApiKey)
        {
            http.DefaultRequestHeaders.Add(CoinGeckoClient.ApiKeyHeader, coinGecko.ApiKey);
        }
    })
    .UseSocketsHttpHandler((handler, _) => handler.PooledConnectionLifetime = TimeSpan.FromMinutes(15))
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
builder.Services.AddHostedService<CoinGeckoCatalog>();

builder.Services.Configure<TwelveDataOptions>(twelveData =>
{
    builder.Configuration.GetSection(TwelveDataOptions.Section).Bind(twelveData);
    twelveData.ApiKey = builder.Configuration["TWELVEDATA_API_KEY"];
});
builder.Services.AddHttpClient<TwelveDataClient>((services, http) =>
    {
        var twelveData = services.GetRequiredService<IOptions<TwelveDataOptions>>().Value;
        http.BaseAddress = twelveData.BaseUrl;
        http.Timeout = TimeSpan.FromSeconds(60);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Marketlens/0.1");
        if (twelveData.HasApiKey)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("apikey", twelveData.ApiKey);
        }
    })
    .UseSocketsHttpHandler((handler, _) => handler.PooledConnectionLifetime = TimeSpan.FromMinutes(15))
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
builder.Services.AddHostedService<TwelveDataHistoryBackfill>();
builder.Services.AddHostedService<SampleStockFeed>();

builder.Services.Configure<ForecastOptions>(forecasts =>
{
    builder.Configuration.GetSection(ForecastOptions.Section).Bind(forecasts);
    if (builder.Configuration["FORECASTER_BASE_URL"] is { Length: > 0 } forecasterBaseUrl)
    {
        forecasts.BaseUrl = new Uri(forecasterBaseUrl);
    }
});
builder.Services.AddHttpClient<ForecasterClient>((services, http) =>
{
    var forecasts = services.GetRequiredService<IOptions<ForecastOptions>>().Value;
    http.BaseAddress = forecasts.BaseUrl;
    http.Timeout = forecasts.RequestTimeout;
});

builder.Services.AddSingleton<QuoteReader>();
builder.Services.AddSingleton<HistoryQuery>();
builder.Services.AddSingleton<QuoteSubscriptions>();
builder.Services.Configure<QuoteBroadcastOptions>(builder.Configuration.GetSection(QuoteBroadcastOptions.Section));
builder.Services.AddHostedService<QuoteBroadcaster>();
builder.Services.AddSignalR().AddJsonProtocol(json => ApiConventions.ConfigureJson(json.PayloadSerializerOptions));
builder.Services.AddApiConventions(builder.Configuration);
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseApiConventions();
app.MapHealthChecks("/health");

var api = app.MapGroup("/api/v1")
    .RequireRateLimiting(ApiConventions.PublicRateLimit)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status429TooManyRequests);
api.MapQuoteEndpoints();
api.MapConversionEndpoints();
api.MapHistoryEndpoints();
api.MapStatusEndpoints();
api.MapLocaleEndpoints();
app.MapHub<QuotesHub>(QuotesHub.Path);

app.Run();
