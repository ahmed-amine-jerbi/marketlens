using System.Text.Json;

using Marketlens.Api.MarketData;

namespace Marketlens.Api.Tests.MarketData;

/// <summary>
/// Against company facts recorded from SEC EDGAR on 2026-11-16, trimmed to the revenue and net income
/// concepts (10-K facts since 2015, 10-Q facts since 2025).
/// </summary>
public sealed class SecEdgarClientTests
{
    [Fact]
    public void Apples_last_five_fiscal_years_come_newest_first_from_its_10_k_filings()
    {
        var years = Parse("sec-companyfacts-aapl.json", years: 5);

        Assert.Equal(
            [new DateOnly(2026, 9, 26), new DateOnly(2025, 9, 27), new DateOnly(2024, 9, 28), new DateOnly(2023, 9, 30), new DateOnly(2022, 9, 24)],
            years.Select(year => year.PeriodEnd));
        Assert.Equal(new FiscalYearFinancials(new DateOnly(2025, 9, 28), new DateOnly(2026, 9, 26), 478_834_000_000m, 132_175_000_000m, years[0].FiledDate), years[0]);
        Assert.Equal(394_328_000_000m, years[4].RevenueUsd);
    }

    [Fact]
    public void Older_years_come_from_the_tag_apple_used_before_asc_606()
    {
        var years = Parse("sec-companyfacts-aapl.json", years: 20);

        var fiscal2015 = Assert.Single(years, year => year.PeriodEnd == new DateOnly(2015, 9, 26));
        Assert.True(fiscal2015.RevenueUsd > 200_000_000_000m);
    }

    [Fact]
    public void Quarterly_figures_in_10_q_filings_are_left_out()
    {
        var years = Parse("sec-companyfacts-aapl.json", years: 20);

        Assert.All(years, year => Assert.InRange(year.PeriodEnd.DayNumber - year.PeriodStart.DayNumber, 350, 380));
    }

    [Fact]
    public void A_bank_reporting_revenues_gets_its_annual_revenue()
    {
        var years = Parse("sec-companyfacts-jpm.json", years: 5);

        Assert.Equal(182_447_000_000m, years[0].RevenueUsd);
        Assert.Equal(57_048_000_000m, years[0].NetIncomeUsd);
        Assert.Equal(5, years.Count);
    }

    [Fact]
    public void A_loss_is_a_negative_net_income()
    {
        var years = Parse("sec-companyfacts-intc.json", years: 5);

        Assert.Equal(-267_000_000m, years[0].NetIncomeUsd);
        Assert.Equal(-18_756_000_000m, years[1].NetIncomeUsd);
    }

    [Fact]
    public void One_tag_supplies_every_year_it_reports_so_definitions_are_not_mixed()
    {
        // Mastercard's newest years are under Revenues (net revenue); its contract-with-customer tag
        // also reports 2021, as a larger gross figure, which must not be picked for that year.
        var years = Parse("sec-companyfacts-ma.json", years: 5);

        var fiscal2021 = Assert.Single(years, year => year.PeriodEnd == new DateOnly(2021, 12, 31));
        Assert.Equal(18_884_000_000m, fiscal2021.RevenueUsd);
    }

    [Fact]
    public void Net_income_falls_back_to_profit_or_loss_when_net_income_loss_is_not_filed()
    {
        var years = Parse("sec-companyfacts-ma.json", years: 5);

        Assert.Equal(14_968_000_000m, years[0].NetIncomeUsd);
        Assert.All(years, year => Assert.NotNull(year.NetIncomeUsd));
    }

    [Fact]
    public void A_document_without_us_gaap_facts_has_no_years()
    {
        using var foreignFiler = JsonDocument.Parse("""{"cik": 1046179, "facts": {"ifrs-full": {}}}""");

        Assert.Empty(SecEdgarClient.ParseAnnualFinancials(foreignFiler.RootElement, years: 5));
    }

    private static IReadOnlyList<FiscalYearFinancials> Parse(string fixture, int years)
    {
        using var companyFacts = JsonDocument.Parse(ProviderFixtures.Read(fixture));
        return SecEdgarClient.ParseAnnualFinancials(companyFacts.RootElement, years);
    }
}
