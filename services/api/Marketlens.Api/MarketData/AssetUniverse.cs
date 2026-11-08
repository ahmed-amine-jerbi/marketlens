using System.Text.Json;

namespace Marketlens.Api.MarketData;

public sealed record CryptoListing(string Symbol, string Name, string BinanceSymbol, string CoinGeckoId);

public sealed record StockListing(string Symbol, string Name, bool IsStreamed);

public sealed record EtfListing(string Symbol, string Name, string Tracks, bool IsStreamed);

/// <summary>
/// The coins, stocks and ETFs Marketlens tracks, read from <c>MarketData/assets.json</c> and checked
/// once at startup. Fiat currencies aren't listed: they are every currency Frankfurter serves.
/// Adding an asset is one entry in that file (plan, Scalability).
/// </summary>
public sealed record AssetUniverse(
    IReadOnlyList<CryptoListing> Crypto,
    IReadOnlyList<StockListing> Stocks,
    IReadOnlyList<EtfListing> Etfs)
{
    /// <summary>Finnhub's free WebSocket streams at most 50 symbols per key; the rest are polled (D27).</summary>
    public const int MaxStreamedStockSymbols = 50;

    /// <summary>Crypto is priced against USDT, which Marketlens treats as 1 USD (D26).</summary>
    public const string BinanceQuoteAsset = "USDT";

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "MarketData", "assets.json");

    private static readonly JsonSerializerOptions FileFormat = new(JsonSerializerDefaults.Web);

    public IEnumerable<StockListing> StreamedStocks => Stocks.Where(stock => stock.IsStreamed);

    public IEnumerable<EtfListing> StreamedEtfs => Etfs.Where(etf => etf.IsStreamed);

    public static AssetUniverse Load(string path)
    {
        using var file = File.OpenRead(path);
        var universe = JsonSerializer.Deserialize<AssetUniverse>(file, FileFormat)
            ?? throw new InvalidDataException($"{path} is empty.");

        var problems = universe.FindProblems().ToList();
        return problems.Count == 0
            ? universe
            : throw new InvalidDataException($"{path} is invalid:{Environment.NewLine}- {string.Join(Environment.NewLine + "- ", problems)}");
    }

    public IEnumerable<string> FindProblems()
    {
        var keys = Crypto.Select(coin => new AssetKey(AssetClass.Crypto, coin.Symbol).ToString())
            .Concat(Stocks.Select(stock => new AssetKey(AssetClass.Stock, stock.Symbol).ToString()))
            .Concat(Etfs.Select(etf => new AssetKey(AssetClass.Etf, etf.Symbol).ToString()))
            .ToList();

        foreach (var key in keys.Where(key => !AssetKey.TryParse(key, out _)))
        {
            yield return $"'{key}' is not a valid asset key (symbols are upper-case letters, digits, '.' or '-').";
        }

        foreach (var duplicate in keys.GroupBy(key => key).Where(group => group.Count() > 1))
        {
            yield return $"'{duplicate.Key}' is listed more than once.";
        }

        foreach (var coin in Crypto.Where(coin => coin.BinanceSymbol != coin.Symbol + BinanceQuoteAsset))
        {
            yield return $"crypto '{coin.Symbol}' must trade as '{coin.Symbol}{BinanceQuoteAsset}' on Binance, not '{coin.BinanceSymbol}'.";
        }

        foreach (var etf in Etfs.Where(etf => string.IsNullOrWhiteSpace(etf.Tracks)))
        {
            yield return $"ETF '{etf.Symbol}' needs 'tracks', so the app never labels the fund as the index itself.";
        }

        var streamedCount = StreamedStocks.Count() + StreamedEtfs.Count();
        if (streamedCount > MaxStreamedStockSymbols)
        {
            yield return $"{streamedCount} stocks and ETFs are marked isStreamed; Finnhub's free WebSocket allows {MaxStreamedStockSymbols}. Set isStreamed to false on the rest; they will be polled and shown as delayed.";
        }
    }
}
