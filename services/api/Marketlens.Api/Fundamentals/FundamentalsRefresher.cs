using Marketlens.Api.Data;
using Marketlens.Api.MarketData;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marketlens.Api.Fundamentals;

public sealed class FundamentalsOptions
{
    public const string Section = "Fundamentals";

    /// <summary>How often a stock's Finnhub profile, figures, analyst counts and earnings date are re-read.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How often a stock's annual figures are re-read from SEC filings (10-Ks come once a year).</summary>
    public TimeSpan SecRefreshInterval { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Pause between provider calls. Finnhub's free 60 calls a minute are shared with quote polling
    /// (25 a minute), so fundamentals stay at about 6 a minute.
    /// </summary>
    public TimeSpan RequestSpacing { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the refresher looks for stocks whose figures are due.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(1);

    public int FinancialYears { get; set; } = 5;

    public int RecommendationMonths { get; set; } = 4;

    /// <summary>How far ahead the earnings calendar is searched for the next date.</summary>
    public int EarningsLookaheadDays { get; set; } = 120;

    /// <summary>Shares are shown only when shares × price is this close to Finnhub's own market cap (D24).</summary>
    public decimal SharesTolerancePct { get; set; } = 5;
}

/// <summary>
/// Keeps every stock's key figures current (roadmap-and-fundamentals, D16): once a day from Finnhub
/// (profile, basic financials, analyst counts, next earnings date) and once a week from SEC filings
/// (annual revenue and net income). Without a Finnhub key nothing is fetched and the endpoint says
/// so; without <c>SEC_USER_AGENT</c> the annual figures are skipped. One stock's failure is logged and
/// the run moves on; a rate limit backs off before the next call.
/// </summary>
public sealed partial class FundamentalsRefresher(
    FinnhubClient finnhub,
    SecEdgarClient secEdgar,
    AssetCatalog catalog,
    AssetUniverse universe,
    QuoteBook quoteBook,
    IServiceScopeFactory scopeFactory,
    IOptions<FundamentalsOptions> options,
    IOptions<FinnhubOptions> finnhubOptions,
    IOptions<SecEdgarOptions> secEdgarOptions,
    TimeProvider timeProvider,
    ILogger<FundamentalsRefresher> logger) : BackgroundService
{
    private const decimal Hundred = 100m;

    private readonly ProviderBackoff backoff = new();

    /// <summary>
    /// Finnhub's share count when it agrees with its own market cap at the same price (within the
    /// tolerance); otherwise null. Some counts are of another share class (Berkshire's B shares
    /// came back as 1.43 million), and a wrong count next to a right market cap misleads.
    /// </summary>
    public static decimal? ConsistentShares(decimal? sharesOutstanding, decimal? marketCapUsd, decimal? priceUsd, decimal tolerancePct)
    {
        if (sharesOutstanding is not { } shares || marketCapUsd is not { } marketCap || priceUsd is not { } price || marketCap <= 0)
        {
            return null;
        }

        var deviationPct = Math.Abs((shares * price / marketCap) - 1m) * Hundred;
        return deviationPct <= tolerancePct ? shares : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!finnhubOptions.Value.HasApiKey)
        {
            LogNoKey(logger);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            // The database briefly gone: logged and retried at the next check. An exception escaping a
            // background service would stop the whole API.
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                LogRunFailed(logger, error, options.Value.CheckInterval);
            }

            await Task.Delay(options.Value.CheckInterval, timeProvider, stoppingToken);
        }
    }

    /// <summary>Refreshes every stock whose Finnhub or SEC figures are older than their interval.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var secCikBySymbol = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var stock in universe.Stocks)
        {
            if (stock.SecCik is { } secCik)
            {
                secCikBySymbol[stock.Symbol] = secCik;
            }
        }

        var fetchedByAsset = await ReadFetchTimesAsync(cancellationToken);
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        foreach (var listing in catalog.ActiveOf(AssetClass.Stock))
        {
            fetchedByAsset.TryGetValue(listing.Id, out var fetched);
            var isFinnhubDue = fetched.FinnhubUtc is not { } finnhubUtc || nowUtc - finnhubUtc >= options.Value.RefreshInterval;
            var isSecDue = secEdgarOptions.Value.HasUserAgent
                && (fetched.SecUtc is not { } secUtc || nowUtc - secUtc >= options.Value.SecRefreshInterval);

            if (isFinnhubDue && listing.ProviderRefs.FinnhubSymbol is { } symbol)
            {
                await TryRefreshAsync(listing, FinnhubClient.SourceName, token => RefreshFromFinnhubAsync(listing, symbol, token), cancellationToken);
            }

            if (isSecDue && secCikBySymbol.TryGetValue(listing.Key.Symbol, out var cik))
            {
                await TryRefreshAsync(listing, SecEdgarClient.SourceName, token => RefreshFromSecAsync(listing, cik, token), cancellationToken);
            }
        }
    }

    private async Task TryRefreshAsync(CatalogEntry listing, string source, Func<CancellationToken, Task> refresh, CancellationToken cancellationToken)
    {
        try
        {
            await refresh(cancellationToken);
            backoff.Reset();
        }
        // One stock's failure (an HTTP error, a rate limit, an unexpected shape, a failed write) is
        // logged and the run moves on; the stored figures keep being served with their age, and the
        // stock is retried at the next check.
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            var delay = backoff.NextDelay((error as ProviderRateLimitedException)?.RetryAfter);
            LogRefreshFailed(logger, error, source, listing.Key, delay);
            await Task.Delay(delay, timeProvider, cancellationToken);
        }
    }

    private async Task RefreshFromFinnhubAsync(CatalogEntry listing, string symbol, CancellationToken cancellationToken)
    {
        var profile = await finnhub.FetchCompanyProfileAsync(symbol, cancellationToken);
        await PauseAsync(cancellationToken);
        var financials = await finnhub.FetchBasicFinancialsAsync(symbol, cancellationToken);
        await PauseAsync(cancellationToken);
        var trends = await finnhub.FetchRecommendationTrendsAsync(symbol, cancellationToken);
        await PauseAsync(cancellationToken);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var nextEarnings = await finnhub.FetchNextEarningsDateAsync(symbol, today, options.Value.EarningsLookaheadDays, cancellationToken);
        await PauseAsync(cancellationToken);

        decimal? priceUsd = quoteBook.TryGet(listing.Key, out var quote) ? quote.PriceUsd : null;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var row = await FindOrAddAsync(db, listing.Id, cancellationToken);
        row.Industry = profile?.Industry;
        row.Country = profile?.Country;
        row.Exchange = profile?.Exchange;
        row.IpoDate = profile?.IpoDate;
        row.WebsiteUrl = profile?.WebsiteUrl?.AbsoluteUri;
        row.MarketCapUsd = profile?.MarketCapUsd;
        row.MarketCapPriceUsd = profile?.MarketCapUsd is null ? null : priceUsd;
        row.SharesOutstanding = ConsistentShares(profile?.SharesOutstanding, profile?.MarketCapUsd, priceUsd, options.Value.SharesTolerancePct);
        row.PeRatio = financials.PeRatio;
        row.EpsTtmUsd = financials.EpsTtmUsd;
        row.DividendYieldPct = financials.DividendYieldPct;
        row.High52WeekUsd = financials.High52WeekUsd;
        row.Low52WeekUsd = financials.Low52WeekUsd;
        row.Beta = financials.Beta;
        row.NextEarningsDate = nextEarnings;
        row.FinnhubFetchedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        // The newest months replace what was stored, so a month Finnhub revised doesn't linger.
        await db.AnalystRecommendations.Where(recommendation => recommendation.AssetId == listing.Id).ExecuteDeleteAsync(cancellationToken);
        db.AnalystRecommendations.AddRange(trends.Take(options.Value.RecommendationMonths).Select(trend => new AnalystRecommendation
        {
            AssetId = listing.Id,
            PeriodDate = trend.Period,
            StrongBuy = trend.StrongBuy,
            Buy = trend.Buy,
            Hold = trend.Hold,
            Sell = trend.Sell,
            StrongSell = trend.StrongSell,
        }));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LogRefreshed(logger, FinnhubClient.SourceName, listing.Key);

        // Stored per stock, not at the end of a run: a restart mid-run would otherwise lose the logos
        // of every stock refreshed before it until their next daily refresh.
        if (profile?.LogoUrl?.AbsoluteUri is { } logoUrl && logoUrl != listing.LogoUrl)
        {
            await catalog.UpdateLogosAsync(new Dictionary<AssetKey, string> { [listing.Key] = logoUrl }, cancellationToken);
        }
    }

    private async Task RefreshFromSecAsync(CatalogEntry listing, string secCik, CancellationToken cancellationToken)
    {
        var years = await secEdgar.FetchAnnualFinancialsAsync(secCik, options.Value.FinancialYears, cancellationToken);
        await PauseAsync(cancellationToken);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var row = await FindOrAddAsync(db, listing.Id, cancellationToken);
        row.SecFetchedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        // Restated years come back with new values; the stored years are replaced, not merged.
        await db.AssetFinancialYears.Where(year => year.AssetId == listing.Id).ExecuteDeleteAsync(cancellationToken);
        db.AssetFinancialYears.AddRange(years.Select(year => new AssetFinancialYear
        {
            AssetId = listing.Id,
            PeriodStartDate = year.PeriodStart,
            PeriodEndDate = year.PeriodEnd,
            RevenueUsd = year.RevenueUsd,
            NetIncomeUsd = year.NetIncomeUsd,
            FiledDate = year.FiledDate,
        }));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LogRefreshed(logger, SecEdgarClient.SourceName, listing.Key);
    }

    private async Task<Dictionary<Guid, (DateTime? FinnhubUtc, DateTime? SecUtc)>> ReadFetchTimesAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        var rows = await db.AssetFundamentals
            .AsNoTracking()
            .Select(row => new { row.AssetId, row.FinnhubFetchedAtUtc, row.SecFetchedAtUtc })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.AssetId, row => (row.FinnhubFetchedAtUtc, row.SecFetchedAtUtc));
    }

    private static async Task<AssetFundamentals> FindOrAddAsync(MarketlensDbContext db, Guid assetId, CancellationToken cancellationToken)
    {
        if (await db.AssetFundamentals.FindAsync([assetId], cancellationToken) is { } existing)
        {
            return existing;
        }

        var added = new AssetFundamentals { AssetId = assetId };
        db.AssetFundamentals.Add(added);
        return added;
    }

    private Task PauseAsync(CancellationToken cancellationToken) => Task.Delay(options.Value.RequestSpacing, timeProvider, cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "No Finnhub key: stock key figures are not fetched")]
    private static partial void LogNoKey(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refreshed {Source} figures for {Asset}")]
    private static partial void LogRefreshed(ILogger logger, string source, AssetKey asset);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refreshing {Source} figures for {Asset} failed; next request in {RetryDelay}")]
    private static partial void LogRefreshFailed(ILogger logger, Exception error, string source, AssetKey asset, TimeSpan retryDelay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The fundamentals check failed; retrying in {CheckInterval}")]
    private static partial void LogRunFailed(ILogger logger, Exception error, TimeSpan checkInterval);
}
