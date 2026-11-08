# Market data ingestion

> Brings prices for fiat currencies, coins, US stocks and index ETFs into Marketlens from five
> providers, keeps the latest price of every asset in memory and builds candle history in
> PostgreSQL, so every other feature reads one provider-neutral source.

**Status:** shipped (API, M2) · **Owner area:** `services/api/Marketlens.Api/MarketData/` ·
**Last updated:** 2026-11-08

## What it does

- Tracks 161 fiat currencies (every currency Frankfurter v2 publishes, including TND), the 30
  coins and 50 stocks/ETFs listed in [assets.json](../../services/api/Marketlens.Api/MarketData/assets.json).
- Prices everything in US dollars. USD is the pivot, always exactly 1; USDT counts as 1 USD.
- Keeps the **latest price** of each asset in memory, with the price its change is measured
  against (previous close, or 24 hours earlier for crypto), its source, time and freshness
  (`live`, `delayed`, `daily`).
- Keeps **candle history**: 1-minute for 3 days, 1-hour for 120 days, 1-day for 5 years.
- Needs **no API keys** to run: fiat and crypto come from keyless providers, and stocks fall back
  to clearly labelled **sample data** until a Finnhub key is set.
- Survives restarts (last prices are saved every minute and restored at startup) and provider
  outages (each worker retries with backoff and the last price ages visibly).

## Walkthrough

1. **Startup.** Migrations run ([DatabaseMigrator](../../services/api/Marketlens.Api/Data/DatabaseMigrator.cs)),
   then [AssetCatalog](../../services/api/Marketlens.Api/MarketData/AssetCatalog.cs#L22) writes the
   listed coins, stocks and ETFs into `assets`, after [AssetUniverse.FindProblems](../../services/api/Marketlens.Api/MarketData/AssetUniverse.cs#L47)
   has refused an invalid file (more than 50 streamed symbols, an ETF without the index it tracks,
   a coin that doesn't trade against USDT). [QuoteSnapshots.RestoreAsync](../../services/api/Marketlens.Api/MarketData/QuoteSnapshots.cs#L43)
   puts the last saved prices back in the quote book.
2. **Fiat.** [FiatRatesWorker](../../services/api/Marketlens.Api/MarketData/FiatRatesWorker.cs#L13)
   syncs the currency list into the catalogue, backfills five years of daily rates once (five
   yearly requests), then every hour reads the last seven days. Each currency's newest day becomes
   its price and the day before its reference ([ToLatestTicks](../../services/api/Marketlens.Api/MarketData/FiatRatesWorker.cs#L35)).
3. **Crypto.** [BinanceLiveFeed](../../services/api/Marketlens.Api/MarketData/BinanceLiveFeed.cs#L16)
   holds one WebSocket to Binance's combined mini-ticker stream for all coins; each frame carries
   the last price and the price 24 hours earlier ([ParseFrame](../../services/api/Marketlens.Api/MarketData/BinanceLiveFeed.cs#L31)).
   [BinanceKlinesBackfill](../../services/api/Marketlens.Api/MarketData/BinanceKlinesBackfill.cs#L13)
   fills candle history from the newest stored bucket (or the retention window on a fresh
   database), at startup and every 6 hours.
4. **Stocks and ETFs, with a Finnhub key.** [FinnhubLiveFeed](../../services/api/Marketlens.Api/MarketData/FinnhubLiveFeed.cs#L14)
   streams trades for up to 50 symbols; [FinnhubQuotePoller](../../services/api/Marketlens.Api/MarketData/FinnhubLiveFeed.cs#L118)
   polls every stock and ETF once per 2 minutes for its previous close (and prices any symbol the
   stream doesn't carry as `delayed`). [TwelveDataHistoryBackfill](../../services/api/Marketlens.Api/MarketData/TwelveDataClient.cs#L96)
   fills daily history when a Twelve Data key is set.
5. **Stocks and ETFs, without a key.** [SampleStockFeed](../../services/api/Marketlens.Api/MarketData/SampleStockFeed.cs#L15)
   writes five years of deterministic sample history (the same on every start) and moves prices
   every 5 seconds, all with source `sample`. When a key appears it deletes its own candles once.
6. **Every accepted price** goes through [QuoteBook.Apply](../../services/api/Marketlens.Api/MarketData/QuoteBook.cs#L36),
   which keeps only newer prices and raises [QuoteUpdated](../../services/api/Marketlens.Api/MarketData/QuoteBook.cs#L29).
   [CandleWriter.Record](../../services/api/Marketlens.Api/MarketData/CandleWriter.cs#L92) rolls
   live and delayed prices into 1m/1h/1d buckets and flushes them every 5 seconds; daily fiat
   rates are not rolled into minute candles.
7. **Housekeeping.** Once a day [CandleWriter.PruneAsync](../../services/api/Marketlens.Api/MarketData/CandleWriter.cs#L157)
   deletes candles past their retention window; every minute `QuoteSnapshots` saves the quote book.

**Alternate flows**
- A provider is down or answers 429/418: the worker logs, waits with [ProviderBackoff](../../services/api/Marketlens.Api/MarketData/ProviderBackoff.cs#L8)
  (1 s doubling to 60 s, ±20% jitter, never shorter than `Retry-After`) and retries; the last
  price stays and its `asOfUtc` shows its age (`GET /api/v1/status` reports it).
- A provider sends an unexpected shape: the frame or response is skipped, or the worker logs and
  retries. No exception escapes a worker, because one would stop the whole API.
- Binance closes the stream (it does every 24 hours): the feed reconnects.

## How it fits together

```mermaid
flowchart LR
  subgraph Providers
    FK[Frankfurter v2]
    BN[Binance REST + stream]
    FH[Finnhub REST + stream]
    TD[Twelve Data]
    CG[CoinGecko]
  end
  FK --> FRW[FiatRatesWorker]
  BN --> BLF[BinanceLiveFeed]
  BN --> BKB[BinanceKlinesBackfill]
  FH --> FLF[FinnhubLiveFeed]
  FH --> FQP[FinnhubQuotePoller]
  TD --> TDB[TwelveDataHistoryBackfill]
  CG --> CGC[CoinGeckoCatalog]
  SSF[SampleStockFeed] -. no Finnhub key .-> QB
  FRW --> QB[QuoteBook]
  BLF --> QB
  FLF --> QB
  FQP --> QB
  QB -- QuoteUpdated --> CW[CandleWriter]
  FRW --> CW
  BKB --> CW
  TDB --> CW
  SSF --> CW
  CW --> PG[(price_candles)]
  QB <--> QS[QuoteSnapshots] <--> SN[(quote_snapshots)]
  FRW --> AC[AssetCatalog]
  CGC --> AC
  AC --> AS[(assets)]
```

## Code walkthrough

- **Asset identity.** [AssetKey](../../services/api/Marketlens.Api/MarketData/AssetKey.cs) is
  `<class>:<symbol>` (`crypto:BTC`, `stock:BRK.B`); every layer passes keys, never provider symbols.
- **Catalogue.** [AssetCatalog](../../services/api/Marketlens.Api/MarketData/AssetCatalog.cs#L22)
  is the only writer of `assets` and holds active assets in a frozen dictionary, so ingestion
  resolves a key to its database id without a query. Removed assets are deactivated, not deleted,
  so their history stays.
- **Adapters** are the only classes that know a provider's JSON: `FrankfurterClient`,
  `BinanceMarketDataClient` (+ `BinanceLiveFeed`), `FinnhubClient` (+ live feed and poller),
  `TwelveDataClient`, `CoinGeckoClient`. Each emits `PriceTick` or `CandleRow`. Keys travel in
  headers (`X-Finnhub-Token`, `Authorization: apikey …`, `x-cg-demo-api-key`), except Finnhub's
  WebSocket, which only accepts a query parameter; that URI is never logged.
- **Quote book.** [QuoteBook](../../services/api/Marketlens.Api/MarketData/QuoteBook.cs#L19) is a
  lock-free dictionary: an older or out-of-order tick is ignored, a tick without a reference keeps
  the known one, and [SetReferencePrice](../../services/api/Marketlens.Api/MarketData/QuoteBook.cs#L81)
  updates a previous close whatever the age of the quote that reported it.
- **Candles.** [CandleWriter.UpsertAsync](../../services/api/Marketlens.Api/MarketData/CandleWriter.cs#L132)
  sends parameterised arrays through `unnest` in batches of 5000 with `ON CONFLICT` merging: the
  first open is kept, high and low widen, close and volume take the newer value. That is why a
  backfill can be re-run and a live bucket can meet a backfilled one safely.
- **Registration order matters** ([Program.cs](../../services/api/Marketlens.Api/Program.cs)):
  hosted services start in order: migrator → Data Protection → catalogue → snapshots → candle
  writer → providers.

## Services and dependencies

| Service | Used for | Where in code | Config / env |
|---|---|---|---|
| Frankfurter v2 | fiat list and daily rates, no key | `FrankfurterClient`, `FiatRatesWorker` | `MarketData:Frankfurter:*` |
| Binance public market data | live crypto, klines; keyless, per-IP request weight | `BinanceMarketDataClient`, `BinanceLiveFeed`, `BinanceKlinesBackfill` | `BINANCE_MARKET_DATA_BASE_URL`, `BINANCE_STREAM_BASE_URL`, `MarketData:Binance:*` |
| Finnhub (free) | live US trades (≤50 symbols, 1 socket), quotes, 60 calls/min | `FinnhubClient`, `FinnhubLiveFeed`, `FinnhubQuotePoller` | `FINNHUB_API_KEY`, `MarketData:Finnhub:*` |
| Twelve Data (free) | daily stock/ETF history, 8 requests/min | `TwelveDataClient`, `TwelveDataHistoryBackfill` | `TWELVEDATA_API_KEY`, `MarketData:TwelveData:*` |
| CoinGecko Demo | coin logos only, one call a day; attribution required | `CoinGeckoClient`, `CoinGeckoCatalog` | `COINGECKO_DEMO_API_KEY` |
| PostgreSQL | assets, candles, snapshots | `Data/`, `CandleWriter`, `QuoteSnapshots` | `DATABASE_URL` |

Free-plan terms (display, redistribution, attribution) are summarised in
[provider-terms.md](../provider-terms.md); a public demo needs a
separate decision (D45).

## Data

- `assets`: `key` (unique), `class`, `symbol`, `name`, `logo_url`, `tracks` (ETFs),
  `provider_refs_*` (Binance symbol, CoinGecko id, Finnhub and Twelve Data symbols), `is_active`.
- `price_candles`: PK `(asset_id, interval, bucket_start_utc)`; `open_usd` … `close_usd`
  `numeric(38,18)`, `volume`, `source`. Retention by interval as above.
- `quote_snapshots`: one row per asset: `price_usd`, `reference_price_usd`, `as_of_utc`, `source`,
  `freshness`.
- Migrations: `InitialMarketData`, `AddQuoteSnapshotFreshness` in
  [Data/Migrations](../../services/api/Marketlens.Api/Data/Migrations/). No personal data.

## API and contracts

Ingestion has no endpoints of its own; its output is read through
[live quotes](live-quotes.md), [currency conversion](currency-conversion.md) and
[price history](price-history-charts.md), and `GET /api/v1/status` reports each source's freshness
(`stockData` says `sample` or `finnhub`).

## Configuration

| Key | Default | Where |
|---|---|---|
| `MarketData:Frankfurter:BaseUrl` / `PollInterval` / `HistoryYears` | `https://api.frankfurter.dev/` / `01:00:00` / `5` | `appsettings.json` |
| `MarketData:Binance:MarketDataBaseUrl` / `StreamBaseUrl` / `GapFillInterval` | `data-api.binance.vision` / `data-stream.binance.vision` / `06:00:00` | `appsettings.json`; env overrides above |
| `MarketData:Finnhub:QuotePollCycle` | `00:02:00` | `FinnhubOptions` |
| `MarketData:TwelveData:RequestSpacing` / `TopUpInterval` | 8 s / 1 day | `TwelveDataOptions` |
| Asset universe | 30 coins, 38 stocks, 12 ETFs | `MarketData/assets.json` |

All keys are optional; every env var is listed in [.env.example](../../.env.example).

## Errors and edge cases

- Invalid `assets.json`: the API refuses to start and lists every problem.
- Non-positive prices are rejected by the quote book; malformed frames are skipped.
- Binance blocks US IPs on `api.binance.com`; the keyless `.vision` hosts are the defaults, and the
  base URLs are configurable.
- Fiat rates from different central banks are published on different days; each currency keeps
  its own date.
- A failed candle flush loses at most a few seconds of live buckets; the next prices and the
  backfills cover the gap. A failed shutdown save means the next start restores the save from the
  previous minute.

## Testing

- Tests in [Marketlens.Api.Tests/MarketData](../../services/api/Marketlens.Api.Tests/MarketData/):
  asset keys, universe rules, quote book, candle upsert/merge/prune, live aggregation, snapshots,
  backoff, and each adapter against fixtures. Frankfurter and Binance fixtures were recorded live;
  Finnhub, Twelve Data and CoinGecko fixtures follow the documented shapes (no keys yet).
- The test host answers provider HTTP from fixtures and removes the Binance WebSocket, so the
  suite never calls a live provider. It needs Docker (Testcontainers PostgreSQL).
- Run: `cd services/api && dotnet test`.
- By hand: `docker compose up -d`, `dotnet run --project Marketlens.Api`, then
  `curl localhost:5080/api/v1/status` and watch the sources fill in.

## Extending it

- **A new coin, stock or ETF:** one entry in `assets.json` (an ETF needs `tracks`; keep ≤50
  `isStreamed`). Restart; the catalogue, backfills and live feeds pick it up.
- **A new provider:** one adapter class that emits `PriceTick` / `CandleRow`, one worker with the
  same loop shape (catch everything except shutdown, back off), its options and env var in
  `Program.cs`, `.env.example` and this doc.
- **A new candle interval:** add it to `CandleInterval`, its column value, `RetentionWindow`,
  `BucketStart`, and `HistoryQuery.Plan`.

## Known limitations and follow-ups

- Finnhub, Twelve Data and CoinGecko adapters are tested only against documented shapes; re-record
  fixtures once P1 keys exist.
- One API instance (ADR 0003): the quote book is in memory; scaling out needs a shared cache.
- Live feeds broadcast unchanged prices too (a mini-ticker each second); harmless, could be trimmed.

## Changelog

- 2026-11-08 — Created (marketlens-v1, M2).
