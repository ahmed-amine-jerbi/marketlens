# Features

One walkthrough per feature: what it does, how it works layer by layer, and how to extend it.

| Feature | Status | Owner area |
|---|---|---|
| [Market data ingestion](market-data-ingestion.md) | shipped (API) | `services/api/Marketlens.Api/MarketData/` |
| [Live quotes](live-quotes.md) | web shipped; mobile M7 | `services/api/Marketlens.Api/Quotes/` |
| [Currency conversion](currency-conversion.md) (reference implementation) | web shipped; mobile M7 | `services/api/Marketlens.Api/Conversions/` |
| [Price history charts](price-history-charts.md) (with the two-point comparison) | web shipped; mobile M7 | `services/api/Marketlens.Api/History/` |
| [Market news](market-news.md) | web shipped; mobile M7 | `services/api/Marketlens.Api/News/`, `apps/web/src/news/` |
| [Key figures](key-figures.md) | web shipped; mobile M7 | `services/api/Marketlens.Api/Fundamentals/`, `apps/web/src/fundamentals/` |
| [Forecasts](forecasts.md) | web shipped; mobile M7 | `services/forecaster/`, `services/api/Marketlens.Api/Forecasts/` |
| [Localization](localization.md) | web shipped; mobile M7 | `packages/shared/src/i18n/`, `apps/web/src/settings/` |
| [Watchlists](watchlists.md) | web shipped (device); sync M5 | `apps/web/src/watchlist/` |

API error types: [../api/problems.md](../api/problems.md). The contract:
[services/api/openapi/v1.json](../../services/api/openapi/v1.json).
