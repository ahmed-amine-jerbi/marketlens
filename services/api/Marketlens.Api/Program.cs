using Marketlens.Api.Data;
using Marketlens.Api.MarketData;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

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

builder.Services.AddHostedService<AssetCatalogSync>();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health");

app.Run();
