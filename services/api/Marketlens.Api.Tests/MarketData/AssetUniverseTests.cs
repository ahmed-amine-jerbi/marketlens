using Marketlens.Api.Data;
using Marketlens.Api.MarketData;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Marketlens.Api.Tests.MarketData;

public sealed class AssetUniverseTests(MarketlensApiFactory factory)
{
    private static readonly AssetUniverse Valid = new(
        [new CryptoListing("BTC", "Bitcoin", "BTCUSDT", "bitcoin"), new CryptoListing("USDT", "Tether", BinanceSymbol: null, "tether")],
        [new StockListing("AAPL", "Apple", IsStreamed: true)],
        [new EtfListing("SPY", "SPDR S&P 500 ETF Trust", "S&P 500", IsStreamed: true)]);

    [Fact]
    public void The_shipped_assets_file_is_valid_and_fits_the_free_stream_limit()
    {
        var universe = AssetUniverse.Load(AssetUniverse.DefaultPath);

        Assert.NotEmpty(universe.Crypto);
        Assert.NotEmpty(universe.Stocks);
        Assert.NotEmpty(universe.Etfs);
        Assert.True(universe.StreamedStocks.Count() + universe.StreamedEtfs.Count() <= AssetUniverse.MaxStreamedStockSymbols);
    }

    [Fact]
    public void FindProblems_accepts_a_valid_universe()
    {
        Assert.Empty(Valid.FindProblems());
    }

    [Fact]
    public void FindProblems_refuses_more_streamed_symbols_than_the_free_websocket_allows()
    {
        var tooManyStreamed = Valid with
        {
            Stocks = Enumerable.Range(1, AssetUniverse.MaxStreamedStockSymbols)
                .Select(number => new StockListing($"S{number}", $"Stock {number}", IsStreamed: true))
                .ToList(),
        };

        var problem = Assert.Single(tooManyStreamed.FindProblems());

        Assert.Contains("51 stocks and ETFs are marked isStreamed", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FindProblems_allows_extra_symbols_that_are_polled_instead_of_streamed()
    {
        var withPolledOverflow = Valid with
        {
            Stocks = Enumerable.Range(1, AssetUniverse.MaxStreamedStockSymbols + 10)
                .Select(number => new StockListing($"S{number}", $"Stock {number}", IsStreamed: number < AssetUniverse.MaxStreamedStockSymbols))
                .ToList(),
        };

        Assert.Empty(withPolledOverflow.FindProblems());
    }

    [Fact]
    public void FindProblems_reports_duplicates_bad_symbols_non_usdt_pairs_and_etfs_without_an_index()
    {
        var broken = new AssetUniverse(
            [new CryptoListing("BTC", "Bitcoin", "BTCUSDT", "bitcoin"), new CryptoListing("BTC", "Bitcoin again", "BTCEUR", "bitcoin")],
            [new StockListing("aapl", "Apple", IsStreamed: true)],
            [new EtfListing("SPY", "SPDR S&P 500 ETF Trust", " ", IsStreamed: true)]);

        var problems = broken.FindProblems().ToList();

        Assert.Contains(problems, problem => problem.Contains("'crypto:BTC' is listed more than once", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("'stock:aapl' is not a valid asset key", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("not 'BTCEUR'", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("ETF 'SPY' needs 'tracks'", StringComparison.Ordinal));
    }

    [Fact]
    public void FindProblems_accepts_tether_without_a_binance_symbol_but_no_other_coin()
    {
        var withTether = Valid with
        {
            Crypto = [.. Valid.Crypto, new CryptoListing("ETH", "Ethereum", BinanceSymbol: null, "ethereum")],
        };

        var problem = Assert.Single(withTether.FindProblems());

        Assert.Contains("crypto 'ETH' must trade as 'ETHUSDT'", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FindProblems_refuses_a_binance_pair_for_tether_itself()
    {
        var tetherPair = Valid with { Crypto = [new CryptoListing("USDT", "Tether", "USDTUSDT", "tether")] };

        var problem = Assert.Single(tetherPair.FindProblems());

        Assert.Contains("is the quote asset itself", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FindProblems_requires_tether_whenever_a_coin_is_priced_in_usdt()
    {
        var withoutTether = Valid with { Crypto = [new CryptoListing("BTC", "Bitcoin", "BTCUSDT", "bitcoin")] };

        var problem = Assert.Single(withoutTether.FindProblems());

        Assert.Contains("(Tether) must be listed", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Fiat_and_tether_have_daily_prices_only()
    {
        Assert.True(AssetKey.Parse("fiat:EUR").HasDailyPricesOnly);
        Assert.True(AssetKey.Tether.HasDailyPricesOnly);
        Assert.False(AssetKey.Parse("crypto:USDC").HasDailyPricesOnly);
        Assert.False(AssetKey.Parse("stock:AAPL").HasDailyPricesOnly);
    }

    [Fact]
    public async Task Startup_writes_the_listed_assets_with_their_provider_ids()
    {
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();

        var bitcoin = await db.Assets.SingleAsync(asset => asset.Key == "crypto:BTC", TestContext.Current.CancellationToken);
        var sp500Etf = await db.Assets.SingleAsync(asset => asset.Key == "etf:SPY", TestContext.Current.CancellationToken);

        var tether = await db.Assets.SingleAsync(asset => asset.Key == "crypto:USDT", TestContext.Current.CancellationToken);

        Assert.Equal("BTCUSDT", bitcoin.ProviderRefs.BinanceSymbol);
        Assert.Equal("bitcoin", bitcoin.ProviderRefs.CoinGeckoId);
        Assert.Null(tether.ProviderRefs.BinanceSymbol);
        Assert.Equal("Tether", tether.Name);
        Assert.Equal("S&P 500", sp500Etf.Tracks);
        Assert.True(sp500Etf.IsActive);
    }
}
