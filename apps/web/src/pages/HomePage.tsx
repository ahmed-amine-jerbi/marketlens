import { useState } from "react";
import { useTranslation } from "react-i18next";

import type { AssetClass, Quote } from "../api/client";
import { useLiveQuotes } from "../api/liveQuotes";
import { useAllAssets, useAssets, useQuotes } from "../api/queries";
import { AssetTable } from "../components/AssetTable";
import { LoadError, TableSkeleton } from "../components/States";
import { ConverterPanel } from "../conversions/ConverterPanel";
import { useSettings } from "../settings/Settings";
import { useWatchlist } from "../watchlist/useWatchlist";

const starterKeys = ["crypto:BTC", "crypto:ETH", "fiat:EUR", "stock:AAPL", "etf:SPY"] as const;
const moveTabs = [
  { id: "currencies", label: "nav.currencies", classes: ["fiat"] },
  { id: "crypto", label: "nav.crypto", classes: ["crypto"] },
  { id: "stocks", label: "nav.stocks", classes: ["stock", "etf"] },
] as const satisfies readonly { id: string; label: string; classes: readonly AssetClass[] }[];
const MOVES_SHOWN = 6;

export function HomePage() {
  const { t } = useTranslation();

  return (
    <div className="grid gap-8 xl:grid-cols-[minmax(0,1fr)_22rem]">
      <div className="min-w-0 space-y-8">
        <h1 className="text-heading font-semibold">{t("home.title")}</h1>
        <Watchlist />
        <BiggestMoves />
      </div>
      <aside className="min-w-0">
        <ConverterPanel />
      </aside>
    </div>
  );
}

function Watchlist() {
  const { t } = useTranslation();
  const { displayCurrency } = useSettings();
  const watchlist = useWatchlist();
  const assets = useAllAssets();
  const watched = watchlist.keys.flatMap(
    (key) => assets.data?.find((asset) => asset.key === key) ?? [],
  );
  const starters = (assets.data ?? []).filter((asset) =>
    starterKeys.some((key) => key === asset.key),
  );

  useQuotes(watchlist.keys, displayCurrency);
  useLiveQuotes(watchlist.keys.slice(0, 50), displayCurrency);

  return (
    <section aria-labelledby="watchlist-title">
      <div className="flex items-baseline justify-between gap-4">
        <h2 id="watchlist-title" className="text-title font-semibold">
          {t("home.watchlist")}
        </h2>
        {watchlist.keys.length > 0 && (
          <p className="text-caption text-muted">{t("home.watchlistDevice")}</p>
        )}
      </div>

      <div className="mt-3 rounded-panel bg-surface px-4 py-1">
        {assets.isError && <LoadError onRetry={() => void assets.refetch()} />}
        {assets.isPending && <TableSkeleton rows={3} />}
        {assets.data && watched.length > 0 && (
          <AssetTable assets={watched} caption={t("home.watchlist")} />
        )}
        {assets.data && watched.length === 0 && (
          <div className="py-5">
            <p className="text-body">{t("home.watchlistEmpty")}</p>
            <p className="mt-4 text-small text-muted">{t("home.suggestions")}</p>
            <ul className="mt-2 flex flex-wrap gap-2">
              {starters.map((asset) => (
                <li key={asset.key}>
                  <button
                    type="button"
                    onClick={() => {
                      watchlist.add(asset.key);
                    }}
                    className="rounded-control border border-divider px-3 py-1.5 text-small hover:bg-background"
                  >
                    + {asset.name}
                  </button>
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>
    </section>
  );
}

/** The largest moves of the day in one market, ranked by the size of the change. */
function BiggestMoves() {
  const { t } = useTranslation();
  const { displayCurrency } = useSettings();
  const [tabId, setTabId] = useState<(typeof moveTabs)[number]["id"]>("crypto");
  const tab = moveTabs.find((candidate) => candidate.id === tabId) ?? moveTabs[1];

  const first = useAssets(tab.classes[0]);
  const second = useAssets(tab.classes[1] ?? tab.classes[0]);
  const assets =
    tab.classes.length > 1 ? [...(first.data ?? []), ...(second.data ?? [])] : (first.data ?? []);
  const quotes = useQuotes(
    assets.map((asset) => asset.key),
    displayCurrency,
  );

  const magnitude = (quote: Quote) => Math.abs(Number(quote.changePct ?? 0));
  const ranked = [...(quotes.data ?? [])]
    .filter((quote) => quote.changePct !== null)
    .sort((left, right) => magnitude(right) - magnitude(left))
    .slice(0, MOVES_SHOWN)
    .flatMap((quote) => assets.find((asset) => asset.key === quote.key) ?? []);

  return (
    <section aria-labelledby="moves-title">
      <h2 id="moves-title" className="text-title font-semibold">
        {t("home.moves")}
      </h2>
      <div role="tablist" aria-label={t("home.moves")} className="mt-3 flex gap-1">
        {moveTabs.map((candidate) => (
          <button
            key={candidate.id}
            type="button"
            role="tab"
            aria-selected={candidate.id === tabId}
            onClick={() => {
              setTabId(candidate.id);
            }}
            className={`rounded-control px-3 py-1.5 text-small ${candidate.id === tabId ? "bg-surface font-semibold" : "text-muted hover:text-ink"}`}
          >
            {t(candidate.label)}
          </button>
        ))}
      </div>
      <div role="tabpanel" className="mt-2 rounded-panel bg-surface px-4 py-1">
        {quotes.isError && <LoadError error={quotes.error} onRetry={() => void quotes.refetch()} />}
        {(first.isPending || quotes.isPending) && !quotes.isError && (
          <TableSkeleton rows={MOVES_SHOWN} />
        )}
        {quotes.isSuccess && ranked.length === 0 && (
          <p className="py-5 text-small text-muted">{t("home.movesEmpty")}</p>
        )}
        {quotes.isSuccess && ranked.length > 0 && (
          <AssetTable assets={ranked} caption={t("home.moves")} />
        )}
      </div>
    </section>
  );
}
