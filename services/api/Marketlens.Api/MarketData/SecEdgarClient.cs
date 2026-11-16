using System.Globalization;
using System.Text.Json;

namespace Marketlens.Api.MarketData;

public sealed class SecEdgarOptions
{
    public const string Section = "MarketData:SecEdgar";

    /// <summary>
    /// From <c>SEC_USER_AGENT</c>: an app name and a contact e-mail ("Marketlens you@example.com"),
    /// which the SEC requires of automated clients. Without it, annual figures aren't fetched.
    /// </summary>
    public string? UserAgent { get; set; }

    public Uri BaseUrl { get; set; } = new("https://data.sec.gov/");

    /// <summary>A company's facts are one or two MB; a slow answer shouldn't hold the refresher for long.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public bool HasUserAgent => !string.IsNullOrWhiteSpace(UserAgent);
}

/// <summary>One fiscal year's revenue and net income in US dollars, as filed in the company's 10-K.</summary>
public sealed record FiscalYearFinancials(
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal RevenueUsd,
    decimal? NetIncomeUsd,
    DateOnly FiledDate);

/// <summary>
/// Adapter for SEC EDGAR's XBRL company facts (public, no key; at most 10 requests a second across
/// all of a client's machines). Reads annual revenue and net income from a company's 10-K facts.
/// </summary>
public sealed class SecEdgarClient(HttpClient http)
{
    public const string SourceName = "sec-edgar";

    // Companies report revenue under different us-gaap tags, and change tags over the years (Apple
    // moved from SalesRevenueNet to the contract-with-customer tag with ASC 606 in 2018; banks report
    // Revenues or revenue net of interest expense). Order matters; see ParseAnnualFinancials.
    private static readonly string[] RevenueTags =
    [
        "RevenueFromContractWithCustomerExcludingAssessedTax",
        "Revenues",
        "RevenuesNetOfInterestExpense",
        "SalesRevenueNet",
        "RevenueFromContractWithCustomerIncludingAssessedTax",
    ];

    private static readonly string[] NetIncomeTags =
    [
        "NetIncomeLoss",
        "NetIncomeLossAvailableToCommonStockholdersBasic",
        "ProfitLoss",
    ];

    /// <summary>A fiscal year lasts 52 or 53 weeks; quarters and year-to-date figures are shorter.</summary>
    private const int MinFiscalYearDays = 350;
    private const int MaxFiscalYearDays = 380;

    /// <returns>The newest <paramref name="years"/> fiscal years, newest first; empty when none are filed.</returns>
    public async Task<IReadOnlyList<FiscalYearFinancials>> FetchAnnualFinancialsAsync(string secCik, int years, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"api/xbrl/companyfacts/CIK{Uri.EscapeDataString(secCik)}.json", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        ProviderRateLimitedException.ThrowIfLimited(response, SourceName);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var companyFacts = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        return ParseAnnualFinancials(companyFacts.RootElement, years);
    }

    /// <summary>
    /// Annual figures from a company-facts document. Only 10-K facts covering a fiscal year count; the
    /// same period appears in several filings (later 10-Ks repeat earlier years), so the latest filed
    /// wins, which also picks up restatements. Per company, the primary tag is the first in order that
    /// reports the newest year; it supplies every year it has, and later tags only fill years it lacks.
    /// Mixing tags year by year would mix definitions (Mastercard's gross 2021 revenue next to net
    /// revenue for later years).
    /// </summary>
    public static IReadOnlyList<FiscalYearFinancials> ParseAnnualFinancials(JsonElement companyFacts, int years)
    {
        if (companyFacts.ValueKind != JsonValueKind.Object
            || !companyFacts.TryGetProperty("facts", out var facts)
            || facts.ValueKind != JsonValueKind.Object
            || !facts.TryGetProperty("us-gaap", out var gaap)
            || gaap.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var revenueByEnd = AnnualValues(gaap, RevenueTags);
        var netIncomeByEnd = AnnualValues(gaap, NetIncomeTags);

        return revenueByEnd.Values
            .OrderByDescending(revenue => revenue.End)
            .Take(years)
            .Select(revenue => new FiscalYearFinancials(
                revenue.Start,
                revenue.End,
                revenue.ValueUsd,
                netIncomeByEnd.TryGetValue(revenue.End, out var netIncome) ? netIncome.ValueUsd : null,
                revenue.Filed))
            .ToList();
    }

    private static Dictionary<DateOnly, AnnualFact> AnnualValues(JsonElement gaap, string[] tagsInOrder)
    {
        var valuesByTag = tagsInOrder
            .Select(tag => (Tag: tag, Values: AnnualValuesOf(gaap, tag)))
            .Where(candidate => candidate.Values.Count > 0)
            .ToList();
        if (valuesByTag.Count == 0)
        {
            return [];
        }

        var newestEnd = valuesByTag.Max(candidate => candidate.Values.Keys.Max());
        var primary = valuesByTag.First(candidate => candidate.Values.ContainsKey(newestEnd));
        var merged = new Dictionary<DateOnly, AnnualFact>(primary.Values);
        foreach (var (_, values) in valuesByTag.Where(candidate => candidate.Tag != primary.Tag))
        {
            foreach (var (end, fact) in values)
            {
                merged.TryAdd(end, fact);
            }
        }

        return merged;
    }

    // {"units": {"USD": [{"start", "end", "val", "form": "10-K", "filed", ...}]}}
    private static Dictionary<DateOnly, AnnualFact> AnnualValuesOf(JsonElement gaap, string tag)
    {
        var latestByPeriod = new Dictionary<(DateOnly Start, DateOnly End), AnnualFact>();
        if (!gaap.TryGetProperty(tag, out var concept)
            || concept.ValueKind != JsonValueKind.Object
            || !concept.TryGetProperty("units", out var units)
            || units.ValueKind != JsonValueKind.Object
            || !units.TryGetProperty("USD", out var usdFacts)
            || usdFacts.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (var fact in usdFacts.EnumerateArray())
        {
            if (ReadAnnualFact(fact) is not { } annual)
            {
                continue;
            }

            var period = (annual.Start, annual.End);
            if (!latestByPeriod.TryGetValue(period, out var seen) || annual.Filed > seen.Filed)
            {
                latestByPeriod[period] = annual;
            }
        }

        return latestByPeriod.Values
            .GroupBy(fact => fact.End)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(fact => fact.Filed).First());
    }

    private static AnnualFact? ReadAnnualFact(JsonElement fact)
    {
        if (fact.ValueKind != JsonValueKind.Object
            || !fact.TryGetProperty("form", out var form) || form.ValueKind != JsonValueKind.String
            || form.GetString() is not { } formName || !formName.StartsWith("10-K", StringComparison.Ordinal)
            || Date(fact, "start") is not { } start
            || Date(fact, "end") is not { } end
            || Date(fact, "filed") is not { } filed
            || !fact.TryGetProperty("val", out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDecimal(out var valueUsd))
        {
            return null;
        }

        var days = end.DayNumber - start.DayNumber;
        return days is >= MinFiscalYearDays and <= MaxFiscalYearDays ? new AnnualFact(start, end, valueUsd, filed) : null;
    }

    private static DateOnly? Date(JsonElement fact, string property) =>
        fact.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private sealed record AnnualFact(DateOnly Start, DateOnly End, decimal ValueUsd, DateOnly Filed);
}
