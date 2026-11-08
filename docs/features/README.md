# Features

One walkthrough per feature: what it does, how it works layer by layer, and how to extend it.

| Feature | Status | Owner area |
|---|---|---|
| [Market data ingestion](market-data-ingestion.md) | shipped (API) | `services/api/Marketlens.Api/MarketData/` |
| [Live quotes](live-quotes.md) | web shipped; mobile M7 | `services/api/Marketlens.Api/Quotes/` |
| [Currency conversion](currency-conversion.md) (reference implementation) | web shipped; mobile M7 | `services/api/Marketlens.Api/Conversions/` |
| [Price history charts](price-history-charts.md) | web shipped; mobile M7 | `services/api/Marketlens.Api/History/` |
| [Localization](localization.md) | web shipped; mobile M7 | `packages/shared/src/i18n/`, `apps/web/src/settings/` |
| [Watchlists](watchlists.md) | web shipped (device); sync M5 | `apps/web/src/watchlist/` |

API error types: [../api/problems.md](../api/problems.md). The contract:
[services/api/openapi/v1.json](../../services/api/openapi/v1.json).
