# 0007 — Crypto priced in US dollars at the daily USDT rate

- **Status:** accepted, 2026-11-09. Supersedes the "USDT is treated as 1 USD" part of
  [0004](0004-provider-adapters.md).

## Context

Binance prices every coin against USDT, and Marketlens counted 1 USDT as 1 US dollar. USDT trades a
little off its peg: Frankfurter's daily rate put it between 0.9981 and 0.9999 USD in Oct–Nov 2026,
and Coinbase showed 0.9989 on 2026-11-09. Every crypto price shown in dollars was therefore off by
up to about 0.2%, enough for users to notice against other sites.

## Decision

- Tether (`crypto:USDT`) is a listed coin priced once a day from Frankfurter's USDT row, which the
  existing keyless fiat worker already receives. A rate outside 0.5–1.5 USD is refused at ingestion.
- Every Binance price is multiplied by the USDT rate before it reaches the quote book or the
  candles, in one module (`MarketData/UsdtRates.cs`): live ticks at the latest rate, candles at the
  rate of their own UTC day, or the latest one before it. Inside the stored history that rate may be
  at most seven days older (a longer gap is history still loading); after the newest rate, the
  newest one is used, as the live feed does.
- With no rate yet, nothing is shown or stored: live ticks are dropped, and the backfill skips the
  fills that need missing history, fills the rest and retries soon.
- Migration `RepriceCryptoInUsd` deleted the stored USDT-basis crypto candles, snapshots and the
  forecasts made from them; the workers refilled them in USD.

## Consequences

- Crypto-to-fiat prices, charts and forecasts are in real dollars; crypto-to-crypto rates are
  unchanged (the rate cancels out).
- The correction is daily: USDT's moves within a day (well under 0.05%) are not followed.
  Frankfurter publishes a day late, so today's prices and buckets use yesterday's rate and are not
  revisited; a later refill of the same day would use that day's own rate. The difference is the
  rate's day-to-day move.
- On a fresh database, crypto appears after the fiat worker's first refresh, and charts after its
  five-year history load.

## Alternatives rejected

- **A live USDT/USD ticker (e.g. Coinbase):** a new provider and failure mode for a 0.1% correction.
- **Rescaling stored candles in place:** needs the daily rates inside the migration; refilling from
  Binance is simpler and keyless.
