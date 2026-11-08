# Marketlens

![Marketlens: live prices, any conversion, honest forecasts](docs/images/banner.svg)

Marketlens shows live prices for currencies, cryptocurrencies, US stocks and world-index ETFs,
converts between any two of them, and charts their history, on the web and on a phone. Each asset
can show a 1–7 day forecast as a shaded range, together with a measured track record of how often
past forecasts were right. Forecasts are estimates, not financial advice.

> **Status:** in development. Working today: live prices for 161 currencies, 30 coins and 50 US
> stocks and index ETFs (clearly marked sample data until a Finnhub key is set), conversion between
> any two, charts from one day to five years, a watchlist, and the web app in English, French and
> German with light and dark themes. Forecasts, accounts, alerts, news and the mobile app follow.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/screenshot-home-dark.png">
  <img alt="The Marketlens home page: a watchlist with Bitcoin, Ethereum, the euro and an S&P 500 ETF, each with its live price, change and freshness, the day's biggest crypto moves, and a converter showing 1 BTC in US dollars." src="docs/images/screenshot-home-light.png">
</picture>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/screenshot-asset-dark.png">
  <img alt="The Bitcoin page: the live price with its 24-hour change, a one-month price chart, and the converter." src="docs/images/screenshot-asset-light.png">
</picture>

## What v1 will do

- Live prices for ~160 fiat currencies, the top ~30 coins, US stocks and index ETFs, each labelled
  with its source and how fresh it is.
- Convert any amount between any two assets (BTC → TND, EUR → JPY, ETH → BTC).
- Charts from one day to five years, in the display currency you choose.
- Forecast ranges with a plain-language track record, and a clear notice when a forecast is no
  better than "no change".
- Browse everything without an account; sign in (email or Google) to sync watchlists and get
  price alerts as push notifications. Market news next to each asset.
- English, French and German, picked from your device and location, changeable at any time.

## Architecture

```mermaid
graph LR
  Web["Web app<br/>React + Vite"] -- "REST + SignalR" --> API
  Mobile["Mobile app<br/>Expo"] -- "REST + SignalR" --> API
  API["API<br/>ASP.NET Core 10"] --> DB[("PostgreSQL 17")]
  API -- "internal HTTP" --> Forecaster["Forecaster<br/>Python + FastAPI"]
  API -- "one connection per provider" --> Providers["Frankfurter · Binance · CoinGecko<br/>Finnhub · Twelve Data"]
```

- The **API** is the only part that talks to data providers. It keeps one upstream connection per
  provider and fans prices out to every viewer, computes every conversion, and owns accounts,
  watchlists and alerts.
- The **forecaster** is stateless: it receives a price series and returns forecast ranges and a
  backtest. It has no public port.
- The **clients** display and format; they never compute prices or call providers.

More in [docs/architecture.md](docs/architecture.md) and the decision records in
[docs/adr/](docs/adr/).

## Repository layout

```text
apps/web/              web app (React, Vite, Tailwind)
apps/mobile/           mobile app (arrives with the mobile milestone)
packages/shared/       design tokens, UI strings, formatting and API types for both apps
services/api/          ASP.NET Core API and its tests (Marketlens.slnx)
services/forecaster/   Python forecasting service and its tests
docs/                  context, architecture, decision records, feature docs, build records
```

## Running it locally

Requirements: Docker Desktop. That's all for running it:

```sh
docker compose up --build
```

Open http://localhost:8080. The first start takes a few minutes to build; currency and crypto
history fills in within a minute or two after that. No API keys are needed. Copy `.env.example` to
`.env` to add provider keys; every variable is described there.

### Working on the code

Also needs Node.js 24+, the .NET 10 SDK and [uv](https://docs.astral.sh/uv/) for Python.

```sh
docker compose up -d postgres mailpit   # database and mail catcher only
npm install

cd services/api && dotnet run --project Marketlens.Api                 # API on http://localhost:5080
npm run dev -w @marketlens/web                                         # web on http://localhost:5173
cd services/forecaster && uv run uvicorn forecaster.app:app --port 8001
```

The web dev server proxies `/api` and `/hubs` to the API, as nginx does in Docker.

Try the API directly:

```sh
curl "localhost:5080/api/v1/conversions?from=crypto:BTC&to=fiat:TND&amount=0.25"
curl "localhost:5080/api/v1/quotes?keys=crypto:BTC,fiat:EUR,stock:AAPL&in=fiat:EUR"
curl "localhost:5080/api/v1/assets/crypto:BTC/history?range=1D"
curl "localhost:5080/api/v1/status"
```

The full contract is [services/api/openapi/v1.json](services/api/openapi/v1.json) (also served at
`/openapi/v1.json`); error types are listed in [docs/api/problems.md](docs/api/problems.md), and
each feature has a walkthrough in [docs/features/](docs/features/README.md).

## Checks

```sh
npm run lint && npm run typecheck && npm test                    # TypeScript (web + shared)
cd services/api && dotnet format --verify-no-changes && dotnet test
cd services/forecaster && uv run ruff check && uv run mypy && uv run pytest
```

End-to-end and accessibility checks run against the Docker stack:

```sh
docker compose up -d --build
npx playwright install chromium        # once
npm run test:e2e -w @marketlens/web    # main flows + axe scans of every page, light and dark
```

CI runs the lint, type and unit checks plus a gitleaks secret scan on every pull request.

## Data sources

Exchange rates from [Frankfurter](https://frankfurter.dev), crypto prices from Binance public
market data, coin names and logos from [CoinGecko](https://www.coingecko.com), stock prices and
news from [Finnhub](https://finnhub.io), stock history from [Twelve Data](https://twelvedata.com).
Each provider's free tier has its own limits and terms; check them before running a public
instance.

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) and
[SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
