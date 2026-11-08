# Marketlens — context

Short, current facts for anyone opening the project cold. History lives in the decisions log
below and in [pipeline/marketlens-v1/](pipeline/marketlens-v1/).

## 1. Purpose

Marketlens is an open-source (MIT) web and mobile app for everyday people who check exchange
rates, convert money, and follow a few coins and stocks. It shows live prices for fiat currencies,
cryptocurrencies, US stocks and world-index ETFs, converts between any two, charts their history,
and draws an honest 1–7 day forecast range with a measured track record. It is not a trading tool
and gives no buy or sell signals.

## 2. Users and key flows

One user type: the *everyday, curious* visitor, signed out by default, optionally signed in.

1. Glance at the watchlist and the day's biggest moves; prices update live.
2. Convert an amount between any two assets (0.25 BTC → TND).
3. Open an asset, change the chart range, see the forecast range and how often past forecasts held.
4. Sign in to sync the watchlist across devices and set a price alert that arrives as a push.
5. Read news next to an asset.

## 3. Stack

| Part | Technology |
|---|---|
| Web | React 19, TypeScript 6.0, Vite, Tailwind 4, TanStack Query, Recharts, react-i18next *(from M3)* |
| Mobile | Expo (expo-router), React Native, Victory Native *(from M7)* |
| Shared TS | `@marketlens/shared`: tokens, i18n messages, formatting, generated API types |
| API | ASP.NET Core 10 (minimal APIs, SignalR), EF Core + Npgsql, ASP.NET Core Identity, xUnit v3 |
| Forecaster | Python 3.12, FastAPI, statsforecast *(from M4)*, uv, Ruff, mypy strict, pytest |
| Data | PostgreSQL 17 |
| Local | Docker Compose (postgres, mailpit; api, forecaster, web join in later milestones) |
| CI | GitHub Actions: lint, typecheck, tests per language, gitleaks |

## 4. Architecture

See [architecture.md](architecture.md) for the diagram, data flows and owners. In one breath: the
clients send intent over REST and SignalR to the API; the API is the only part that talks to data
providers, does every price conversion, stores history in PostgreSQL, owns accounts, watchlists
and alerts, and asks the internal forecaster for forecasts once a day.

```text
apps/web/  apps/mobile/       clients (display + format only)
packages/shared/              tokens, i18n, format.ts, generated API types
services/api/                 Marketlens.Api (feature folders) + Marketlens.Api.Tests
services/forecaster/          forecaster/ (FastAPI app) + tests/
docs/                         this file, architecture.md, adr/, features/, pipeline/
```

## 5. Features index

| Feature | Owner | Status | Doc |
|---|---|---|---|
| Repository foundation (toolchains, Compose, CI) | root, `services/*`, `packages/shared` | shipped (M1) | — |
| Market data ingestion | `services/api/Marketlens.Api/MarketData/` | planned (M2) | `features/market-data-ingestion.md` |
| Live quotes | `Quotes/`, `QuotesHub` | planned (M2) | `features/live-quotes.md` |
| Currency conversion | `Conversions/` | planned (M2–M3) | `features/currency-conversion.md` |
| Price history charts | `History/`, web/mobile `PriceChart` | planned (M2–M3) | `features/price-history-charts.md` |
| Localization | `packages/shared/src/i18n`, `Locale/` | planned (M3) | `features/localization.md` |
| Forecasts | `services/forecaster`, `Forecasts/` | planned (M4) | `features/forecasts.md` |
| Accounts and sign-in | `Accounts/` | planned (M5) | `features/accounts-and-sign-in.md` |
| Watchlists | `Watchlists/` | planned (M3, sync M5) | `features/watchlists.md` |
| Price alerts | `Alerts/` | planned (M6) | `features/price-alerts.md` |
| Market news | `News/` | planned (M6) | `features/market-news.md` |

## 6. Conventions

[CLAUDE.md](../CLAUDE.md) is the rulebook; the `review-ready-code` skill is the code standard.
Project-specific:
- One API project organised by feature folders, no MediatR or layer projects ([ADR 0002](adr/0002-single-api-with-feature-folders.md)).
- Money and prices: `decimal` in C#, `Decimal` in Python, strings in JSON and TypeScript.
- Timestamps are UTC and named `…Utc`.
- Test names read as specs: `Health_reports_healthy_when_the_api_has_started`, `test_health_reports_healthy`.
- Reference implementation (from M2/M3): the `Conversions` feature end to end.

## 7. Glossary

| Term | Meaning |
|---|---|
| **Asset** | Anything with a price: a fiat currency, a coin, a stock or an ETF. Never "instrument" or "ticker". |
| **Asset key** | Stable id `<class>:<symbol>`: `fiat:EUR`, `crypto:BTC`, `stock:AAPL`, `etf:SPY`. |
| **Asset class** | `fiat`, `crypto`, `stock`, `etf`. |
| **Quote** | An asset's latest price in a given currency, with its change, source, time and freshness. |
| **Price tick** | One normalised price observation from a provider, in USD. |
| **Candle** | Open/high/low/close for one interval bucket (`1m`, `1h`, `1d`). |
| **Range** | The chart's time span: `1D`, `1W`, `1M`, `3M`, `1Y`, `5Y`. |
| **Display currency** | The asset prices are shown in (`in=` on the API); default from locale, user-changeable. |
| **Pivot** | USD. Every asset has a USD price; any pair is the ratio of two USD prices. USDT counts as 1 USD. |
| **Conversion** | A rate and converted amount between two assets, computed only by the API. |
| **Leg** | One of the two USD prices a conversion uses; the conversion is as fresh as its older leg. |
| **Freshness** | `live` (streamed), `delayed` (polled), `daily` (published once a day). |
| **Change basis** | What `changePct` compares with: `previous-close` (stocks, ETFs, fiat) or `rolling-24h` (crypto). |
| **Provider** | An external data source (Frankfurter, Binance, CoinGecko, Finnhub, Twelve Data). |
| **Adapter** | The one class that talks to one provider and emits provider-neutral types. |
| **Forecast** | A predicted range for the next horizon steps: `median`, 80% and 95% bands. |
| **Horizon** | How far a forecast reaches: 7 days for crypto, 5 business days otherwise. |
| **Baseline** | The "no change" forecast; a model is shown only if it beats it in the backtest. |
| **Backtest** | Rolling re-forecast of past data to measure a model before showing it. |
| **Track record** | How often the real price stayed inside the 80% band, from the backtest or from live results. |
| **Watchlist** | A user's ordered list of asset keys; on the device when signed out, synced when signed in. |
| **Alert rule** | "Asset above/below threshold in currency"; states `armed` and `triggered`. |
| **Push subscription** | A device or browser that can receive push (`web` or `expo`). |
| **Notification** | The stored record of a triggered alert, shown in the app and sent by push. |
| **Locale suggestion** | The API's guess of language and display currency for a visitor who hasn't chosen. |

## 8. Style

Calm, clear, honest. Cool neutrals, one cobalt accent, green and red only for up and down (always
with ▲/▼ or +/−). IBM Plex Sans, tabular figures for prices, sentence case. Tokens:
[`packages/shared/src/tokens.ts`](../packages/shared/src/tokens.ts). Full direction in the plan's
Visual design section.

## 9. Environments and config

- **Local:** `docker compose up -d` (PostgreSQL :5432, Mailpit :8025), API `dotnet run` on :5080,
  forecaster `uv run uvicorn` on :8001. No keys needed.
- **Public demo:** after v1, gated on checking each provider's display terms.
- **Config:** non-secret defaults in `services/api/Marketlens.Api/appsettings.json`; secrets and
  per-deployment values from environment variables, all listed in [`.env.example`](../.env.example)
  (`.env` is git-ignored).

## 10. Decisions log

Decisions D1–D42 with reasons: [pipeline/marketlens-v1/decisions.md](pipeline/marketlens-v1/decisions.md)
and the plan's Decisions section. Architecture decisions: [adr/](adr/).

- 2026-11-08 TypeScript pinned to 6.0.3, not 7.0, because typescript-eslint 8.72 supports `<6.1`.
- 2026-11-08 Light-theme "up" green `#1A7F51` (plan had `#1C8656`, which failed WCAG AA on the
  background).

## 11. Extension recipes

Filled in as the first example of each lands (M2 onward): add an asset, add a provider adapter,
add an endpoint, add a language, add a table and migration, add a background job.

## 12. Tech debt

- `minimatch` moderate advisory in dev-only lint tooling (via ESLint / typescript-eslint); waiting
  on an upstream release.

## 13. Open questions

- Provider free-tier limits and display terms to confirm on the providers' own pages (P1, before
  M2).
