namespace Marketlens.Api.MarketData;

/// <summary>How current a price is. Ordered from freshest to slowest.</summary>
public enum Freshness
{
    /// <summary>Streamed from the provider as it trades.</summary>
    Live,

    /// <summary>Polled, or delayed by the provider.</summary>
    Delayed,

    /// <summary>Published once a day (central-bank reference rates).</summary>
    Daily,
}

/// <summary>
/// One price observation from a provider adapter, already normalised to USD. This is the only shape
/// ingestion hands to the rest of the API.
/// </summary>
/// <param name="ReferencePriceUsd">
/// The price the change is measured against: 24 hours earlier for crypto, the previous close for
/// everything else (D40). Null when the provider doesn't supply one.
/// </param>
public sealed record PriceTick(
    AssetKey Asset,
    decimal PriceUsd,
    decimal? ReferencePriceUsd,
    DateTime ObservedAtUtc,
    string Source,
    Freshness Freshness);
