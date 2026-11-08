import { useDeferredValue, useState } from "react";
import { useTranslation } from "react-i18next";
import { useParams } from "react-router";

import type { Asset, AssetClass } from "../api/client";
import { useLiveQuotes } from "../api/liveQuotes";
import { useAssets, useQuotes } from "../api/queries";
import { AssetTable } from "../components/AssetTable";
import { LoadError, TableSkeleton } from "../components/States";
import { useSettings } from "../settings/Settings";
import { NotFoundPage } from "./NotFoundPage";

const markets = {
  currencies: {
    classes: ["fiat"],
    title: "markets.currenciesTitle",
    intro: "markets.currenciesIntro",
  },
  crypto: { classes: ["crypto"], title: "markets.cryptoTitle", intro: "markets.cryptoIntro" },
  stocks: { classes: ["stock", "etf"], title: "markets.stocksTitle", intro: "markets.stocksIntro" },
} as const satisfies Record<
  string,
  { classes: readonly AssetClass[]; title: string; intro: string }
>;

type Market = keyof typeof markets;
const isMarket = (value: string | undefined): value is Market =>
  value !== undefined && value in markets;

/** One market (currencies, crypto, stocks and ETFs) with live prices and a filter. */
export function MarketsPage() {
  const { market } = useParams();
  if (!isMarket(market)) {
    return <NotFoundPage />;
  }

  return <Market key={market} market={market} />;
}

function Market({ market }: { market: Market }) {
  const { t } = useTranslation();
  const { displayCurrency } = useSettings();
  const [filter, setFilter] = useState("");
  const deferredFilter = useDeferredValue(filter.trim().toLowerCase());
  const page = markets[market];

  const first = useAssets(page.classes[0]);
  const second = useAssets(page.classes[1] ?? page.classes[0]);
  const assets: Asset[] =
    page.classes.length > 1 ? [...(first.data ?? []), ...(second.data ?? [])] : (first.data ?? []);
  const shown = assets.filter(
    (asset) =>
      deferredFilter === "" ||
      asset.symbol.toLowerCase().includes(deferredFilter) ||
      asset.name.toLowerCase().includes(deferredFilter),
  );

  const keys = assets.map((asset) => asset.key);
  useQuotes(keys, displayCurrency);
  // Live updates only for what is on screen and cheap: currencies move once a day.
  useLiveQuotes(
    market === "currencies" ? [] : shown.slice(0, 50).map((asset) => asset.key),
    displayCurrency,
  );

  const isLoading = first.isPending || second.isPending;
  const failed = first.isError ? first : second.isError ? second : null;

  return (
    <section aria-labelledby="market-title">
      <h1 id="market-title" className="text-heading font-semibold">
        {t(page.title)}
      </h1>
      <p className="mt-1 max-w-prose text-small text-muted">
        {t(page.intro, { count: assets.length })}
      </p>

      <label className="mt-6 block max-w-xs">
        <span className="sr-only">{t("markets.filter")}</span>
        <input
          type="search"
          value={filter}
          placeholder={t("markets.filter")}
          onChange={(event) => {
            setFilter(event.target.value);
          }}
          className="w-full rounded-control border border-divider bg-surface px-3 py-2 text-small placeholder:text-muted"
        />
      </label>

      <div className="mt-4 rounded-panel bg-surface px-4 py-1">
        {failed && <LoadError error={failed.error} onRetry={() => void failed.refetch()} />}
        {isLoading && <TableSkeleton rows={10} />}
        {!isLoading && !failed && shown.length === 0 && (
          <p className="py-6 text-small text-muted">{t("search.noResults", { query: filter })}</p>
        )}
        {!isLoading && !failed && shown.length > 0 && (
          <AssetTable assets={shown} caption={t(page.title)} />
        )}
      </div>
    </section>
  );
}
