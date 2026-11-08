# Features

One walkthrough per feature: what it does, how it works layer by layer, and how to extend it.

| Feature | Status | Owner area |
|---|---|---|
| [Market data ingestion](market-data-ingestion.md) | shipped (API) | `services/api/Marketlens.Api/MarketData/` |
| [Live quotes](live-quotes.md) | API shipped; screens in M3/M7 | `services/api/Marketlens.Api/Quotes/` |
| [Currency conversion](currency-conversion.md) (reference implementation) | API shipped; screens in M3/M7 | `services/api/Marketlens.Api/Conversions/` |
| [Price history charts](price-history-charts.md) | API shipped; charts in M3/M7 | `services/api/Marketlens.Api/History/` |

API error types: [../api/problems.md](../api/problems.md). The contract:
[services/api/openapi/v1.json](../../services/api/openapi/v1.json).
