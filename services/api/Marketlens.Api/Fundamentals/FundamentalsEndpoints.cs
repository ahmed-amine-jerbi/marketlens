using Marketlens.Api.Conversions;
using Marketlens.Api.Data;
using Marketlens.Api.Http;
using Marketlens.Api.MarketData;
using Marketlens.Api.Quotes;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marketlens.Api.Fundamentals;

public enum FundamentalsStatus
{
    /// <summary>Figures have been fetched; each may still be missing (null).</summary>
    Available,

    /// <summary>A stock, a Finnhub key is set, and its figures haven't been fetched yet.</summary>
    Pending,

    /// <summary>A stock, but no Finnhub key is set, so nothing is fetched (no sample figures are invented).</summary>
    NoProviderKey,

    /// <summary>Not a stock: no free source covers ETFs, coins or currencies.</summary>
    NotCovered,
}

public sealed record CompanyProfileResponse(string? Industry, string? Country, string? Exchange, DateTime? IpoDateUtc, string? WebsiteUrl);

/// <summary>Money (market cap, EPS, 52-week range) in the display currency; the rest unitless.</summary>
public sealed record KeyFiguresResponse(
    decimal? MarketCap,
    decimal? SharesOutstanding,
    decimal? PeRatio,
    decimal? EpsTtm,
    decimal? DividendYieldPct,
    decimal? High52Week,
    decimal? Low52Week,
    decimal? Beta,
    DateTime? NextEarningsDateUtc);

/// <summary>One fiscal year from the company's 10-K, in the display currency; net income can be negative.</summary>
public sealed record FinancialYearResponse(DateTime PeriodEndUtc, decimal Revenue, decimal? NetIncome);

/// <summary>How many analysts rated the stock strong buy … strong sell in a month, as Finnhub reports it.</summary>
public sealed record AnalystTrendResponse(DateTime PeriodUtc, int StrongBuy, int Buy, int Hold, int Sell, int StrongSell);

public sealed record FundamentalsResponse(
    FundamentalsStatus Status,
    AssetKey In,
    DateTime? AsOfUtc,
    CompanyProfileResponse? Profile,
    KeyFiguresResponse? Figures,
    IReadOnlyList<FinancialYearResponse> FinancialYears,
    IReadOnlyList<AnalystTrendResponse> AnalystTrends,
    IReadOnlyList<string> Sources);

/// <summary>
/// <c>GET /api/v1/assets/{key}/fundamentals</c>: a stock's key figures, its annual revenue and net
/// income, and what analysts reported, in the display currency at that currency's latest price.
/// The market cap moves with the stock's live price (D24).
/// </summary>
public static class FundamentalsEndpoints
{
    public static RouteGroupBuilder MapFundamentalsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/assets/{key}/fundamentals", GetFundamentals).WithName("GetAssetFundamentals").WithTags("Fundamentals")
            .WithSummary("Get a stock's key figures, annual revenue and net income, and analyst recommendation counts")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return api;
    }

    /// <summary>Stored figures are served even after the key is removed, with their age (D20).</summary>
    public static FundamentalsStatus StatusOf(AssetClass assetClass, bool hasStoredFigures, bool hasProviderKey) => (assetClass, hasStoredFigures, hasProviderKey) switch
    {
        (not AssetClass.Stock, _, _) => FundamentalsStatus.NotCovered,
        (_, true, _) => FundamentalsStatus.Available,
        (_, _, true) => FundamentalsStatus.Pending,
        _ => FundamentalsStatus.NoProviderKey,
    };

    /// <summary>The stored market cap moved by the stock's price change since it was fetched (D24).</summary>
    public static decimal? CurrentMarketCapUsd(decimal? storedMarketCapUsd, decimal? priceAtFetchUsd, decimal? currentPriceUsd) =>
        (storedMarketCapUsd, priceAtFetchUsd, currentPriceUsd) switch
        {
            ({ } marketCap, { } atFetch, { } current) when atFetch > 0 => ConversionCalculator.RoundToSignificantDigits(marketCap * current / atFetch),
            ({ } marketCap, _, _) => marketCap,
            _ => null,
        };

    private static async Task<Results<Ok<FundamentalsResponse>, ProblemHttpResult>> GetFundamentals(
        AssetCatalog catalog,
        QuoteBook quoteBook,
        MarketlensDbContext db,
        IOptions<FinnhubOptions> finnhubOptions,
        string key,
        string? @in,
        CancellationToken cancellationToken)
    {
        if (!AssetKey.TryParse(key, out var asset))
        {
            return ApiProblems.InvalidParameter("key", "Use an asset key such as stock:AAPL.");
        }

        if (!catalog.TryGet(asset, out var listing))
        {
            return ApiProblems.UnknownAsset(asset);
        }

        if (!QuoteEndpoints.TryParseDisplayCurrency(catalog, @in, out var displayIn, out var invalidIn))
        {
            return invalidIn;
        }

        if (asset.Class != AssetClass.Stock)
        {
            return TypedResults.Ok(Empty(FundamentalsStatus.NotCovered, displayIn));
        }

        if (!quoteBook.TryGet(displayIn, out var displayQuote))
        {
            return ApiProblems.RateUnavailable(displayIn);
        }

        var stored = await db.AssetFundamentals.AsNoTracking().SingleOrDefaultAsync(row => row.AssetId == listing.Id, cancellationToken);
        var status = StatusOf(asset.Class, stored is not null, finnhubOptions.Value.HasApiKey);
        if (stored is null)
        {
            return TypedResults.Ok(Empty(status, displayIn));
        }

        var years = await db.AssetFinancialYears.AsNoTracking()
            .Where(year => year.AssetId == listing.Id)
            .OrderByDescending(year => year.PeriodEndDate)
            .ToListAsync(cancellationToken);
        var trends = await db.AnalystRecommendations.AsNoTracking()
            .Where(recommendation => recommendation.AssetId == listing.Id)
            .OrderByDescending(recommendation => recommendation.PeriodDate)
            .ToListAsync(cancellationToken);

        var displayPriceUsd = displayQuote.PriceUsd;
        decimal? InDisplay(decimal? amountUsd) => amountUsd is { } amount ? ConversionCalculator.ConvertSignedAmount(amount, displayPriceUsd) : null;
        decimal? currentPriceUsd = quoteBook.TryGet(asset, out var assetQuote) ? assetQuote.PriceUsd : null;

        var figures = new KeyFiguresResponse(
            MarketCap: InDisplay(CurrentMarketCapUsd(stored.MarketCapUsd, stored.MarketCapPriceUsd, currentPriceUsd)),
            SharesOutstanding: stored.SharesOutstanding,
            PeRatio: stored.PeRatio,
            EpsTtm: InDisplay(stored.EpsTtmUsd),
            DividendYieldPct: stored.DividendYieldPct,
            High52Week: InDisplay(stored.High52WeekUsd),
            Low52Week: InDisplay(stored.Low52WeekUsd),
            Beta: stored.Beta,
            NextEarningsDateUtc: MidnightUtc(stored.NextEarningsDate));
        var profile = new CompanyProfileResponse(stored.Industry, stored.Country, stored.Exchange, MidnightUtc(stored.IpoDate), stored.WebsiteUrl);
        var sources = new List<string>();
        if (stored.FinnhubFetchedAtUtc is not null)
        {
            sources.Add(FinnhubClient.SourceName);
        }

        if (years.Count > 0)
        {
            sources.Add(SecEdgarClient.SourceName);
        }

        return TypedResults.Ok(new FundamentalsResponse(
            status,
            displayIn,
            AsOfUtc(stored),
            profile,
            figures,
            years.Select(year => new FinancialYearResponse(
                MidnightUtc(year.PeriodEndDate),
                ConversionCalculator.ConvertSignedAmount(year.RevenueUsd, displayPriceUsd),
                InDisplay(year.NetIncomeUsd))).ToList(),
            trends.Select(trend => new AnalystTrendResponse(
                MidnightUtc(trend.PeriodDate),
                trend.StrongBuy,
                trend.Buy,
                trend.Hold,
                trend.Sell,
                trend.StrongSell)).ToList(),
            sources));
    }

    private static FundamentalsResponse Empty(FundamentalsStatus status, AssetKey displayIn) =>
        new(status, displayIn, AsOfUtc: null, Profile: null, Figures: null, FinancialYears: [], AnalystTrends: [], Sources: []);

    // The older of the two sources' fetch times: every figure is at least that current.
    private static DateTime? AsOfUtc(AssetFundamentals stored)
    {
        DateTime?[] fetchTimes = [stored.FinnhubFetchedAtUtc, stored.SecFetchedAtUtc];
        return fetchTimes.Min() is { } oldest ? DateTime.SpecifyKind(oldest, DateTimeKind.Utc) : null;
    }

    // Dates travel as midnight UTC, like forecast days and history points.
    private static DateTime MidnightUtc(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private static DateTime? MidnightUtc(DateOnly? date) => date is { } value ? MidnightUtc(value) : null;
}
