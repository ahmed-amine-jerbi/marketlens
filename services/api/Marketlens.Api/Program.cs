using Marketlens.Api.Data;
using Marketlens.Api.MarketData;

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
builder.Services.AddSingleton<CandleWriter>();

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

builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health");

app.Run();
