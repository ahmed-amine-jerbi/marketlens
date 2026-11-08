import { Star } from "lucide-react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";

import type { Asset } from "../api/client";
import { useQuote } from "../api/liveQuotes";
import { useSettings } from "../settings/Settings";
import { useWatchlist } from "../watchlist/useWatchlist";
import { Change, Freshness, Price } from "./QuoteParts";

/**
 * A list of assets with live prices in the display currency. Rows are links; the star adds or
 * removes the asset from the watchlist. Prices come from the live quote store, so each row
 * re-renders only when its own price moves.
 */
export function AssetTable({ assets, caption }: { assets: readonly Asset[]; caption: string }) {
  const { t } = useTranslation();

  return (
    <table className="w-full border-collapse text-small">
      <caption className="sr-only">{caption}</caption>
      <thead>
        <tr className="border-b border-divider text-start text-caption text-muted">
          <th scope="col" className="py-2 pe-2 text-start font-normal">
            {t("table.name")}
          </th>
          <th scope="col" className="px-2 py-2 text-end font-normal">
            {t("table.price")}
          </th>
          <th scope="col" className="hidden px-2 py-2 text-end font-normal sm:table-cell">
            {t("table.change")}
          </th>
          <th scope="col" className="hidden px-2 py-2 text-start font-normal lg:table-cell">
            {t("table.updated")}
          </th>
          <th scope="col" className="w-10 py-2">
            <span className="sr-only">{t("watch.add")}</span>
          </th>
        </tr>
      </thead>
      <tbody>
        {assets.map((asset) => (
          <AssetRow key={asset.key} asset={asset} />
        ))}
      </tbody>
    </table>
  );
}

function AssetRow({ asset }: { asset: Asset }) {
  const { displayCurrency } = useSettings();
  const quote = useQuote(asset.key, displayCurrency);

  return (
    <tr className="border-b border-divider last:border-b-0">
      <td className="py-3 pe-2">
        <Link
          to={`/assets/${encodeURIComponent(asset.key)}`}
          className="group flex min-w-0 items-center gap-3"
        >
          {asset.logoUrl ? (
            <img
              src={asset.logoUrl}
              alt=""
              className="size-6 shrink-0 rounded-full"
              loading="lazy"
            />
          ) : (
            <span
              aria-hidden
              className="grid size-6 shrink-0 place-items-center rounded-full bg-background text-caption font-semibold text-muted"
            >
              {asset.symbol.slice(0, 1)}
            </span>
          )}
          <span className="min-w-0">
            <span className="block truncate font-semibold group-hover:underline">{asset.name}</span>
            <span className="block text-caption text-muted">{asset.symbol}</span>
          </span>
        </Link>
      </td>
      <td className="px-2 py-3 text-end">
        {quote ? (
          <Price quote={quote} />
        ) : (
          <span className="inline-block h-4 w-16 animate-pulse rounded-control bg-background" />
        )}
        {quote && (
          <span className="block text-caption sm:hidden">
            <Change quote={quote} />
          </span>
        )}
      </td>
      <td className="hidden px-2 py-3 text-end sm:table-cell">
        {quote && <Change quote={quote} />}
      </td>
      <td className="hidden px-2 py-3 text-caption lg:table-cell">
        {quote && <Freshness quote={quote} assetClass={asset.class} />}
      </td>
      <td className="py-3 text-end">
        <WatchToggle assetKey={asset.key} name={asset.name} />
      </td>
    </tr>
  );
}

/** A star that adds or removes the asset from the watchlist; announces its state to screen readers. */
export function WatchToggle({
  assetKey,
  name,
  withLabel = false,
}: {
  assetKey: string;
  name: string;
  withLabel?: boolean;
}) {
  const { t } = useTranslation();
  const watchlist = useWatchlist();
  const isWatched = watchlist.has(assetKey);

  return (
    <button
      type="button"
      aria-pressed={isWatched}
      aria-label={
        withLabel ? undefined : t(isWatched ? "watch.removeLabel" : "watch.addLabel", { name })
      }
      onClick={() => {
        if (isWatched) {
          watchlist.remove(assetKey);
        } else {
          watchlist.add(assetKey);
        }
      }}
      className={`inline-flex min-h-10 min-w-10 items-center justify-center gap-2 rounded-control ${withLabel ? "border border-divider px-3 text-small" : ""} hover:bg-background`}
    >
      <Star
        aria-hidden
        className={`size-4 ${isWatched ? "fill-accent text-accent" : "text-muted"}`}
        strokeWidth={1.5}
      />
      {withLabel && <span>{t(isWatched ? "watch.remove" : "watch.add")}</span>}
    </button>
  );
}
