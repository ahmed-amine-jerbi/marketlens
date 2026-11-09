# Marketlens — context

Short, current facts for anyone opening the project cold. History lives in the decisions log
below and in the architecture decision records ([adr/](adr/)).

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
| Forecaster | Python 3.12, FastAPI, statsforecast 2.1, pandas, uv, Ruff, mypy strict, pytest |
| Data | PostgreSQL 17 |
| Local | Docker Compose: postgres, mailpit, api, forecaster (internal only), web (nginx) |
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
docs/                         this file, architecture.md, adr/, features/, api/
```

## 5. Features index

Full list with walkthroughs: [features/README.md](features/README.md).

| Feature | Owner | Status | Doc |
|---|---|---|---|
| Repository foundation (toolchains, Compose, CI) | root, `services/*`, `packages/shared` | shipped (M1) | — |
| Market data ingestion | `services/api/Marketlens.Api/MarketData/` | shipped (M2) | [features/market-data-ingestion.md](features/market-data-ingestion.md) |
| Live quotes | `Quotes/`, `apps/web/src/api/liveQuotes.ts` | web shipped (M3); mobile M7 | [features/live-quotes.md](features/live-quotes.md) |
| Currency conversion (reference implementation) | `Conversions/`, `apps/web/src/conversions/` | web shipped (M3); mobile M7 | [features/currency-conversion.md](features/currency-conversion.md) |
| Price history charts | `History/`, `apps/web/src/components/PriceChart.tsx` | web shipped (M3); mobile M7 | [features/price-history-charts.md](features/price-history-charts.md) |
| Localization | `packages/shared/src/i18n`, `apps/web/src/settings/`, `Locale/` | web shipped (M3); GeoLite2 D47 | [features/localization.md](features/localization.md) |
| Forecasts | `services/forecaster`, `Forecasts/`, `apps/web/src/forecasts/` | web shipped (M4); mobile M7 | [features/forecasts.md](features/forecasts.md) |
| Accounts and sign-in | `Accounts/` | planned (M5) | `features/accounts-and-sign-in.md` |
| Watchlists | `apps/web/src/watchlist/` (device); `Watchlists/` (M5) | web shipped on the device (M3); sync M5 | [features/watchlists.md](features/watchlists.md) |
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
| **Pivot** | USD. Every asset has a USD price; any pair is the ratio of two USD prices. |
| **USDT rate** | The USD price of one USDT (Tether, `crypto:USDT`), from Frankfurter's daily rate. Binance quotes coins in USDT; ingestion multiplies by this rate, so crypto prices are in real USD (ADR 0007). |
| **Conversion** | A rate and converted amount between two assets, computed only by the API. |
| **Leg** | One of the two USD prices a conversion uses; the conversion is as fresh as its older leg. |
| **Freshness** | `live` (streamed), `delayed` (polled), `daily` (published once a day). |
| **Change basis** | What `changePct` compares with: `previous-close` (stocks, ETFs, fiat, Tether) or `rolling-24h` (other crypto). |
| **Provider** | An external data source (Frankfurter, Binance, CoinGecko, Finnhub, Twelve Data). |
| **Adapter** | The one class that talks to one provider and emits provider-neutral types. |
| **Forecast** | A predicted range for the next horizon steps: `median`, 80% and 95% bands. |
| **Horizon** | How far a forecast reaches: 7 days for crypto, 5 business days otherwise. |
| **Baseline** | The "no change" forecast (the last close carried forward). The chosen model must beat it in the backtest; otherwise the range is the baseline's, and the page says so (D57). |
| **Backtest** | Rolling re-forecast of past data to measure a model before showing it. |
| **Track record** | How often the real price stayed inside the 80% band and the typical miss next to the baseline's, from the backtest or, once Marketlens's own forecasts have been checked across the whole 8-week window, from live results. |
| **Forecast range** | The 80% and 95% bands drawn on the chart after the last close; "forecast range" in UI copy, never "prediction". Sized by calibration on the asset's own past moves, scaled to its current volatility. |
| **Typical miss** | UI words for mean absolute % error of the median. |
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

- **Whole app:** `docker compose up --build` → http://localhost:8080 (nginx serves the web app and
  proxies `/api` and `/hubs` to the API on the Compose network 172.28.0.0/16, which the API trusts
  for forwarded headers).
- **Development:** `docker compose up -d postgres mailpit` (PostgreSQL :5432, Mailpit :8025), API `dotnet run` on :5080
  (applies migrations, fills fiat and crypto history within minutes, sample stocks immediately;
  OpenAPI at `/openapi/v1.json`, live hub at `/hubs/quotes`), forecaster `uv run uvicorn` on
  :8001, web `npm run dev -w @marketlens/web` on :5173 (proxies `/api` and `/hubs` to :5080). No keys
  needed. E2E + axe: `npm run test:e2e -w @marketlens/web` against :8080.
- **Public demo:** after v1, gated on checking each provider's display terms.
- **Config:** non-secret defaults in `services/api/Marketlens.Api/appsettings.json`; secrets and
  per-deployment values from environment variables, all listed in [`.env.example`](../.env.example)
  (`.env` is git-ignored).

## 10. Decisions log

Architecture decisions with their reasons and rejected alternatives: [adr/](adr/). Other dated
decisions:

- 2026-11-08 TypeScript pinned to 6.0.3, not 7.0, because typescript-eslint 8.72 supports `<6.1`.
- 2026-11-08 Light-theme "up" green `#1A7F51` (plan had `#1C8656`, which failed WCAG AA on the
  background).
- 2026-11-08 D44 build M2 without keys (sample stocks); D45 provider terms recorded, public demo
  needs its own decision; D46 provider ids as typed columns (Npgsql couldn't read EF Core 10 JSON
  complex types); D47 GeoLite2 moves to M3.
- 2026-11-08 Web: prices ≥ 10 show two decimals, 1–10 up to four, below 1 four significant digits;
  public rate limit 600/min per IP (one page view makes about a dozen calls); built files under
  `/static/` so they never collide with `/assets/<key>` pages; D48 push to GitHub after M3.
- 2026-11-08 Rates and amounts keep 15 significant digits (ratios of USD prices otherwise show
  rounding residue).
- 2026-11-09 Forecasts (M4): forecast range shown in the display currency at its latest rate (D51);
  statuses `available` / `pending` / `insufficient-history` (D52, D57); decimals as strings on the
  internal contract too (D53); 8 weekly backtest windows, strict baseline rule (D54); 730 days of
  complete closes, checked every 10 minutes (D55); points as rows, 90-day retention (D56); the
  range is always shown with honest wording, drawn on 1W / 1M / 3M (D57, D59); after the code
  review, a failing asset waits for its next close and the live record must span the window (D60).
- 2026-11-09 Crypto priced in real USD at Frankfurter's daily USDT rate (ADR 0007); Tether listed
  as a daily-priced coin; migration `RepriceCryptoInUsd` refilled crypto history in USD and reset
  forecasts. Forecast ranges calibrated on each asset's own past moves (volatility-scaled
  conformal) instead of the models' intervals: on real prices the 80% range held 79% (coins) and
  81% (currencies) out of sample, crypto ranges ~30% narrower. Rejected: a live Coinbase USDT
  ticker (new provider for a 0.1% correction), new models for the centre line (no measurable gain).

## 11. Extension recipes

- **Add a coin, stock or ETF:** one entry in `services/api/Marketlens.Api/MarketData/assets.json`
  (ETFs need `tracks`; at most 50 `isStreamed`). Restart.
- **Add a market data provider:** one adapter emitting `PriceTick` / `CandleRow`, one worker with
  the standard loop (catch everything except shutdown, `ProviderBackoff`), options + env var in
  `Program.cs` and `.env.example`, fixtures recorded into `Marketlens.Api.Tests/Fixtures/` and
  served by `ProviderFixtures`. See [features/market-data-ingestion.md](features/market-data-ingestion.md).
- **Add an endpoint:** copy the `Conversions` shape: a static `Map…Endpoints` extension in the
  feature folder, handlers returning `Results<Ok<T>, ProblemHttpResult>` with `.WithSummary`,
  errors from `ApiProblems`, rules in a pure class with unit tests; map it in `Program.cs` under
  the `/api/v1` group (rate limited).
- **Add a page:** a component in `apps/web/src/pages/`, a lazy route in `app/routes.tsx`, strings
  in all three `packages/shared/src/i18n/*.json` (the messages test enforces it), a nav entry in
  `app/Layout.tsx` if it belongs in the menu, and an E2E + axe entry in `apps/web/e2e/app.spec.ts`.
- **Change the API contract:** change the code, run `MARKETLENS_UPDATE_OPENAPI=1 dotnet test` in
  `services/api` to rewrite `openapi/v1.json`, review the diff, then `npm run generate:api` and
  `npm run lint:api`. CI fails if either file is stale.
- **Add a table:** entity + `IEntityTypeConfiguration` in `Data/`, `DbSet` on
  `MarketlensDbContext`, `dotnet ef migrations add <Name> --project Marketlens.Api --output-dir
  Data/Migrations`; migrations apply at startup.
- **Add a forecasting model:** one entry in `CANDIDATE_MODELS`
  (`services/forecaster/forecaster/forecast.py`); the backtest decides when it is used. Time it on
  730 real closes first. See [features/forecasts.md](features/forecasts.md).
- **Add a background job:** a `BackgroundService` in its feature folder, registered in
  `Program.cs` after the services it depends on (hosted services start in order); never let an
  exception escape the loop.

## 12. Tech debt

- `minimatch` moderate advisory in dev-only lint tooling (via ESLint / typescript-eslint); waiting
  on an upstream release.
- Finnhub, Twelve Data and CoinGecko fixtures follow documented shapes, not recorded responses;
  re-record once the P1 keys exist.
- Live feeds re-send unchanged prices each second (Binance re-reports them); could be trimmed.
- Forecasts: an asset whose two-year history is too sparse for the forecaster stays "being
  prepared" (the API doesn't check the forecaster's minimum count); display currencies convert
  every forecast day at today's rate.

## 13. Open questions

- Public demo: Finnhub and Twelve Data free plans forbid public display / redistribution, Binance
  forbids commercial relays, and sources disagree on Binance attribution (D45,
  [provider-terms.md](provider-terms.md)). Decide before any demo.
- IP-country lookup (GeoLite2, P4) needs a MaxMind key and account id (D47, M3).
