# 0004 — One adapter per data provider, USD as the pivot

- **Status:** accepted, 2026-11-08
- **Decision records:** D5, D26, D27, D28, D36 in [decisions.md](../pipeline/marketlens-v1/decisions.md)
  and [the plan](../pipeline/marketlens-v1/plan.md)

## Context

Prices come from five providers with different shapes, limits and keys: Frankfurter (fiat, daily,
no key), Binance (crypto, streamed, no key), CoinGecko (coin catalogue, key), Finnhub (US stocks
streamed for up to 50 symbols, news, key) and Twelve Data (stock history, key). Free tiers allow one
WebSocket per key and few calls per minute. Deployers of the open-source project bring their own
keys or none.

## Decision

- Each provider sits behind exactly one adapter in `MarketData/` that emits provider-neutral types
  (`PriceTick`, candles, catalogue entries). Nothing outside the adapter knows the provider's JSON.
- Only the API talks to providers: one upstream connection per provider, fanned out to every viewer.
- Every asset has a USD price. Any pair is `priceUsd(A) / priceUsd(B)`, computed only by
  `ConversionCalculator`; USDT is treated as 1 USD and the UI says so.
- Without stock keys, `SampleStockFeed` supplies clearly labelled sample data.

## Consequences

- Swapping or adding a provider is one new adapter and its config.
- A pair is only as fresh as its slower leg (a per-second coin against a daily fiat rate); the API
  reports that rather than hiding it.
- USDT drifting from 1 USD shows up as a small error in crypto-to-fiat rates.

## Alternatives rejected

- **Clients calling providers directly:** leaks keys, multiplies rate-limit use per viewer, and
  spreads price maths across apps.
- **Storing every pair:** the number of pairs grows with the square of the asset count.
