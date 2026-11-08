# 0003 — PostgreSQL as the only store in v1

- **Status:** accepted, 2026-11-08
- **Decision record:** D25 in [the plan](../pipeline/marketlens-v1/plan.md)

## Context

The app needs durable storage for assets, price candles, forecasts and accounts, plus fast access
to the latest prices. About 150 assets are tracked. One API instance runs in v1.

## Decision

PostgreSQL 17 only. Latest quotes live in memory in the API (`QuoteBook`) and are snapshotted to
`quote_snapshots` so a restart starts warm. Candles are ordinary tables keyed by
`(asset_id, interval, bucket_start_utc)`, with retention by interval (1m for 3 days, 1h for 120
days, 1d forever).

## Consequences

- One database to run, back up and reason about; `docker compose up` stays small.
- Scaling past one API instance needs a shared quote cache and a SignalR backplane (Redis or Azure
  SignalR). That is the first thing to add if traffic demands it.

## Alternatives rejected

- **Redis for latest quotes:** a second store for data that fits in one process's memory.
- **TimescaleDB:** candle volumes for ~150 assets are small enough for plain tables and
  primary-key range scans.
