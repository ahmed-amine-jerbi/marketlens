# Provider limits and terms — official pages, read 2026-11-08

Research agent, official pages only. Several pages render with JavaScript; where the figure came
from the search engine's indexed copy of the official page, confidence is marked lower.

| Provider | Free limits | Public display | Redistribution (our server relays to viewers) | Attribution | Confidence |
|---|---|---|---|---|---|
| Finnhub | 60 calls/min + 30 calls/s cap; WebSocket 50 symbols; company news 1 year, North American companies | "strictly for personal use unless explicitly stated otherwise" | "not redistribute or share access to data or derived results" without written approval | not found | ToS high, limits medium |
| Twelve Data (Basic) | 8 credits/min, 800/day; US only; ETF EOD on Basic unclear | **No**: "Internal non-display usage" confirmed; external display from Venture ($499/mo) | add-on / agreement ("US Equities External distribution from $2,400/year"); Free Tier commercial use prohibited (§2.3(l)) | where licensed | high |
| CoinGecko (Demo) | 100 calls/min, 10,000 calls/month; daily/hourly history 1 year | yes, intended use | no re-distribution or syndication of API access; cache refreshed at least every 24 h | **required**: "Powered by CoinGecko" (legible, ≥10 pt) or "Data provided by CoinGecko" linked, close to the data | high |
| Binance public market data | keyless; per-IP request weight; 429 then 418 bans (2 min–3 days) | not stated for live API data | main ToU (search-index copy, low confidence): no "data feeding or streaming services" using Binance market data without written consent, nor apps profiting from it (incl. ads); Vision datasets CC BY-NC-SA 4.0, non-commercial | Vision: attribute "Binance Vision"; ToU copy: don't name Binance as source of relayed prices without agreement (unconfirmed) | Vision high, ToU low/medium |
| Frankfurter | no key, "no quotas", abuse rate limiting; caching encouraged | yes, "public data, shared for information" | blended rates "free to use"; each source's terms apply | none required | high |

Sources (with quotes) are in the research report: finnhub.io/terms-of-service, finnhub.io/pricing,
twelvedata.com/pricing, twelvedata.com/pricing.md, twelvedata.com/pricing-business,
twelvedata.com/terms, coingecko.com/en/api/pricing, coingecko.com/en/api_terms,
brand.coingecko.com/resources/attribution-guide, docs.coingecko.com/docs/common-errors-rate-limit,
developers.binance.com (market data only FAQ, REST limits), data.binance.vision/Binance_Vision-Terms_of_Use.pdf,
frankfurter.dev, frankfurter.dev/license.

## Consequences
- Local and self-hosted personal use (what v1 builds) fits every provider; each deployer brings
  their own keys under their own terms. Nothing in M2 changes.
- A **public demo** cannot show Twelve Data or Finnhub data on free plans (no display /
  no redistribution) and must stay non-commercial for Binance. Options before a demo: written
  approval from Finnhub, a paid Twelve Data display licence, or showing stocks only as labelled
  sample data in the demo.
- CoinGecko: daily catalogue refresh = ~30 calls/month, far inside 10,000; show "Data provided by
  CoinGecko" linked, next to coin names/logos.
- Binance attribution is contradictory between sources (Vision requires it, a ToU copy forbids
  naming Binance without agreement). Open question for the user before any public demo.
