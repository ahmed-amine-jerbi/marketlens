import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useParams } from "react-router";

import { ApiProblemError } from "../api/client";
import { useLiveQuotes, useQuote } from "../api/liveQuotes";
import { chartRanges, useAsset, type ChartRange } from "../api/queries";
import { WatchToggle } from "../components/AssetTable";
import { PriceChart } from "../components/PriceChart";
import { Change, Freshness, Price } from "../components/QuoteParts";
import { LoadError } from "../components/States";
import { ConverterPanel } from "../conversions/ConverterPanel";
import { useSettings } from "../settings/Settings";

/** One asset: its live price in the display currency, its change, its chart and a converter. */
export function AssetPage() {
  const { key = "" } = useParams();
  const { t } = useTranslation();
  const settings = useSettings();
  // An asset shown in itself is always 1; show the dollar instead (and the dollar in euros).
  const displayCurrency =
    key !== settings.displayCurrency
      ? settings.displayCurrency
      : key === "fiat:USD"
        ? "fiat:EUR"
        : "fiat:USD";
  const [range, setRange] = useState<ChartRange>("1M");
  const detail = useAsset(key, displayCurrency);
  useLiveQuotes(detail.isSuccess ? [key] : [], displayCurrency);
  const quote = useQuote(key, displayCurrency);

  if (detail.error instanceof ApiProblemError && detail.error.status === 404) {
    return (
      <div className="max-w-xl">
        <h1 className="text-heading font-semibold">{t("asset.notFound", { key })}</h1>
        <Link to="/" className="mt-4 inline-block text-accent underline underline-offset-4">
          {t("errors.backHome")}
        </Link>
      </div>
    );
  }

  if (detail.isError) {
    return <LoadError error={detail.error} onRetry={() => void detail.refetch()} />;
  }

  if (detail.isPending) {
    return (
      <div aria-hidden className="space-y-4">
        <div className="h-8 w-56 animate-pulse rounded-control bg-surface" />
        <div className="h-12 w-72 animate-pulse rounded-control bg-surface" />
        <div className="h-80 animate-pulse rounded-panel bg-surface" />
      </div>
    );
  }

  const { asset } = detail.data;
  return (
    <div className="grid gap-8 xl:grid-cols-[minmax(0,1fr)_22rem]">
      <article className="min-w-0">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div>
            <h1 className="text-heading font-semibold">
              {asset.name} <span className="text-title font-normal text-muted">{asset.symbol}</span>
            </h1>
            {asset.tracks && (
              <p className="mt-1 text-small text-muted">
                {t("asset.tracks", { index: asset.tracks })}
              </p>
            )}
          </div>
          <WatchToggle assetKey={asset.key} name={asset.name} withLabel />
        </div>

        {quote ? (
          <div className="mt-4">
            <p className="text-hero font-semibold leading-tight">
              <Price quote={quote} />{" "}
              <span className="text-title font-normal text-muted">{quote.in.split(":")[1]}</span>
            </p>
            <p className="mt-1 text-small">
              <Change quote={quote} withBasis />
            </p>
            <p className="mt-1 text-small">
              <Freshness quote={quote} assetClass={asset.class} />
            </p>
          </div>
        ) : (
          <p className="mt-4 text-small text-muted">{t("asset.noPriceYet")}</p>
        )}

        <div
          role="radiogroup"
          aria-label={t("asset.chartLabel", {
            name: asset.name,
            range: t(`asset.ranges.${range}`),
          })}
          className="mt-6 flex flex-wrap gap-1"
        >
          {chartRanges.map((candidate) => (
            <button
              key={candidate}
              type="button"
              role="radio"
              aria-checked={candidate === range}
              aria-label={t(`asset.ranges.${candidate}`)}
              onClick={() => {
                setRange(candidate);
              }}
              className={`min-w-11 rounded-control border px-3 py-1.5 text-small ${candidate === range ? "border-accent bg-accent font-semibold text-surface" : "border-divider hover:bg-surface"}`}
            >
              {candidate}
            </button>
          ))}
        </div>
        <PriceChart asset={asset} range={range} displayIn={displayCurrency} />
      </article>

      <aside className="min-w-0">
        <ConverterPanel key={asset.key} initialFrom={asset.key} />
      </aside>
    </div>
  );
}
