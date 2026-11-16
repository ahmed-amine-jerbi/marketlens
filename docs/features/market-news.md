# Market news

> The latest stock-market and crypto headlines on the home page and a News page, and the last
> week's news about a company on its stock page, each linking to its publisher.

**Status:** shipped on the web; mobile in M7 · **Owner area:** `services/api/Marketlens.Api/News/`,
`apps/web/src/news/` · **Last updated:** 2026-11-16

## What it does

- **Home page:** a "Latest news" section shows the 5 newest headlines across stock markets and
  crypto, with a "More news" link.
- **News page** (`/news`, in the menu): "Markets" and "Crypto" tabs, 30 headlines each.
- **Stock pages:** "News about {company}" lists the last week's headlines that mention it (8 shown).
- Each article shows its picture (when the publisher has one), the headline linking to the
  publisher in a new tab, the source, how long ago it was published, and a two-line summary.
- News comes from Finnhub's free plan and is in English. Finnhub's "general" category ("Markets")
  is broad: mostly markets, sometimes wider world news.
- Without a Finnhub key the sections say news needs one. Coins, funds and currencies have no
  company news.

## Walkthrough

1. The home page's [LatestNews](../../apps/web/src/pages/HomePage.tsx#L203) calls
   `GET /api/v1/news?category=all&limit=5`; the [NewsPage](../../apps/web/src/pages/NewsPage.tsx#L16)
   calls `?category=markets` or `?category=crypto` with `limit=30`; a stock page's
   [CompanyNews](../../apps/web/src/pages/AssetPage.tsx#L188) calls `GET /api/v1/assets/stock:AAPL/news?limit=8`.
2. [GetMarketNews](../../services/api/Marketlens.Api/News/NewsEndpoints.cs#L44) /
   [GetAssetNews](../../services/api/Marketlens.Api/News/NewsEndpoints.cs#L63) validate the category,
   key and limit and ask [NewsService](../../services/api/Marketlens.Api/News/NewsService.cs#L72).
3. The service answers from its in-memory cache when the list is under 15 minutes old; otherwise
   one refresh calls Finnhub through [FinnhubClient](../../services/api/Marketlens.Api/MarketData/FinnhubClient.cs#L120)
   and stores the new list. `all` merges the markets and crypto lists, newest first.
4. [NewsList](../../apps/web/src/news/NewsList.tsx#L17) renders the articles, or the matching state.

**Alternate flows:** Finnhub fails → the last list is served; if there never was one, "News couldn't
be fetched … try again in a few minutes". No key → "News needs a Finnhub API key". Quiet day → an
empty-list sentence. A picture that fails to load disappears.

## How it fits together

```mermaid
sequenceDiagram
  actor Reader
  participant Web as Home / News page / stock page
  participant API as GET /api/v1/news, /assets/{key}/news
  participant S as NewsService (15-min cache)
  participant F as Finnhub (free)
  Reader->>Web: opens the page
  Web->>API: category=all&limit=5
  API->>S: GetMarketNewsAsync(All)
  alt list under 15 minutes old
    S-->>API: cached articles
  else stale or missing
    S->>F: news?category=general / crypto
    F-->>S: articles
  end
  API-->>Web: status + items (newest first)
  Web-->>Reader: headlines; links open the publisher
```

## Code walkthrough

- **Adapter:** [FinnhubClient.ParseNews](../../services/api/Marketlens.Api/MarketData/FinnhubClient.cs#L140)
  keeps an article only with an id, a headline, an absolute **https** link and a time; drops a
  picture that isn't https (the article stays); cuts headlines at 300, summaries at 600 and sources
  at 120 characters; drops repeated ids; sorts newest first.
- **Service:** [NewsService](../../services/api/Marketlens.Api/News/NewsService.cs) — one cache entry
  per category and per company; [GetCachedAsync](../../services/api/Marketlens.Api/News/NewsService.cs#L104)
  refreshes under one lock, so readers arriving together cause one call, and on failure logs and
  serves the last list. Company news reads the last `CompanyNewsDays` (7) days.
- **Endpoints:** [NewsEndpoints](../../services/api/Marketlens.Api/News/NewsEndpoints.cs) — `category`
  is `markets` (default), `crypto` or `all`; `limit` 1–50 (default 20); a non-stock asset answers
  `not-covered`.
- **Web:** [NewsList.tsx](../../apps/web/src/news/NewsList.tsx) owns the article row and every state;
  the age ("51 min. ago") ticks with the shared clock in its own small component. Pictures load
  lazily, decode asynchronously and are requested **without a referrer**, so publishers don't learn
  which Marketlens page a reader was on. Links open in a new tab with `noopener noreferrer`, and say
  so to screen readers. The News page tabs use the shared arrow-key rule (`rovingFocus.ts`).

## Services and dependencies

| Service | Used for | Where in code | Config / env |
|---|---|---|---|
| Finnhub (free) | `/news?category=general\|crypto`, `/company-news` (North American companies, 1 year) | `FinnhubClient` | `FINNHUB_API_KEY` |
| Publishers' image hosts | article pictures, loaded by the reader's browser | `NewsList` | nginx `img-src https:` |

Calls happen only when someone reads news: at most 8 an hour for the two categories, and one per
viewed stock per 15 minutes, inside Finnhub's 60 a minute shared with quote polling. Finnhub's free
data is for personal use ([provider terms](../provider-terms.md)); headlines and summaries belong to
their publishers, as the page says.

## Data

Nothing is stored: lists live in the API's memory for 15 minutes and are lost on restart (the next
reader fetches them again).

## API and contracts

| Method + path | Returns |
|---|---|
| `GET /api/v1/news?category=markets\|crypto\|all&limit=1–50` | `NewsResponse` |
| `GET /api/v1/assets/{key}/news?limit=1–50` | `NewsResponse` (`not-covered` for non-stocks) |

```json
{
  "status": "available | unavailable | no-provider-key | not-covered",
  "items": [
    { "id": 7531214, "headline": "U.S. crude oil declines to $87 a barrel …", "summary": "Oil prices are falling …",
      "source": "CNBC", "url": "https://www.cnbc.com/…", "imageUrl": "https://image.cnbcfm.com/…",
      "publishedAtUtc": "2026-11-16T16:02:11Z" }
  ]
}
```

Errors: 400 `invalid-parameter` (`category`, `limit`, `key`), 404 `unknown-asset`. Public and
rate-limited like every `/api/v1` route. OpenAPI tag `News`.

## Configuration

| Key | Default | Where |
|---|---|---|
| `FINNHUB_API_KEY` | empty (no news) | env, `.env.example` |
| `News:CacheDuration` | `00:15:00` | `appsettings.json` |
| `News:CompanyNewsDays` | 7 | `appsettings.json` |
| Shown on the home page / News page / stock page | 5 / 30 / 8 | `LATEST_NEWS_SHOWN`, `ARTICLES_SHOWN`, `COMPANY_NEWS_SHOWN` |

## Errors and edge cases

- Finnhub down, throttled or answering oddly: logged; the last list keeps being served.
- An article without a picture or summary (about half of CoinDesk's) renders as text only.
- A link or picture that isn't https is dropped; the CSP allows `https:` images only.
- Very long headlines and summaries are cut by the API and clamped to two lines on the page.

## Testing

- [NewsTests](../../services/api/Marketlens.Api.Tests/News/NewsTests.cs): parser against answers
  recorded on 2026-11-16 (order, https, missing pictures, unsafe links, caps, repeats); service
  cache (one call within 15 minutes, a new one after), `all` merge order, a failed refresh keeps the
  last list, a never-fetched list reads `unavailable`, company news covers the last week; endpoints
  without a key, `not-covered`, problem details.
- E2E in [app.spec.ts](../../apps/web/e2e/app.spec.ts): home "Latest news" → "More news"; News page
  tabs with arrow keys; axe on `/news` in both themes.
- By hand: `curl "localhost:8080/api/v1/news?category=all&limit=5"`; open the home page, `/news`, a
  stock page.

## Extending it

- **A new topic tab** (Finnhub also has `forex` and `merger`): one entry in `NewsCategory` and the
  `FinnhubCategory` map, one in `TryParseCategory`, a tab in `NewsPage` and its string in en/fr/de;
  regenerate the contract and types.
- **News for coins:** filter the crypto category by the coin's name or symbol in `NewsService`, and
  let `GetAssetNews` answer for crypto.

## Known limitations and follow-ups

- English only; no translation.
- The cache is per API instance and in memory (one instance today, ADR 0003).
- Pictures come from publishers' servers, which see readers' IP addresses (no referrer is sent).
- Finnhub's "general" category includes some non-market news.

## Changelog

- 2026-11-16 — Created (roadmap-and-fundamentals, PR D, at the user's request).
