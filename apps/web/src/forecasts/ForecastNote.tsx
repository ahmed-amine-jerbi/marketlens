import { formatDate, formatPercent } from "@marketlens/shared/format";
import type { UseQueryResult } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";

import type { Forecast } from "../api/client";
import { useSettings } from "../settings/Settings";

/**
 * The forecast's track record in one plain sentence under the chart (R14, D57): how often the real
 * price stayed inside the 80% range and how a typical miss compares with assuming no change, then
 * the date, the not-advice notice and the method page. A win too small to show at two decimals is
 * worded as "about as well as" no change. Also explains every state without a range.
 */
export function ForecastNote({
  forecast,
  assetName,
  isRangeDrawn,
}: {
  forecast: UseQueryResult<Forecast>;
  assetName: string;
  isRangeDrawn: boolean;
}) {
  const { t } = useTranslation();
  const { language } = useSettings();

  if (forecast.isPending) {
    return (
      <div aria-hidden className="mt-4 h-10 max-w-2xl animate-pulse rounded-control bg-surface" />
    );
  }

  if (forecast.isError) {
    return (
      <p className="mt-4 text-small text-muted">
        {t("forecast.loadFailed")}{" "}
        <button
          type="button"
          onClick={() => void forecast.refetch()}
          className="font-semibold text-ink underline underline-offset-4"
        >
          {t("errors.retry")}
        </button>
      </p>
    );
  }

  const { status, trackRecord, createdAtUtc } = forecast.data;
  if (status === "insufficient-history") {
    return (
      <p className="mt-4 text-small text-muted">
        {t("forecast.insufficient", { name: assetName })}
      </p>
    );
  }

  if (status === "pending" || !trackRecord || !createdAtUtc) {
    return <p className="mt-4 text-small text-muted">{t("forecast.pending")}</p>;
  }

  const weeks = Number(trackRecord.windowWeeks);
  const record = {
    source: t(trackRecord.source === "live" ? "forecast.sourceLive" : "forecast.sourceBacktest", {
      weeks,
    }),
    coverage: formatPercent(trackRecord.coverage80Pct, language),
    error: formatPercent(trackRecord.meanErrorPct, language, 2),
    baselineError: formatPercent(trackRecord.baselineErrorPct, language, 2),
  };

  // A win too small to see at two decimals is not presented as a win.
  const wording = !trackRecord.beatsBaseline
    ? "forecast.notBetter"
    : record.error === record.baselineError
      ? "forecast.aboutTheSame"
      : "forecast.beats";

  return (
    <div className="mt-4 max-w-2xl space-y-2">
      <p className="text-body">{t(wording, record)}</p>
      {!isRangeDrawn && <p className="text-small text-muted">{t("forecast.onlyOnRanges")}</p>}
      <p className="text-small text-muted">
        {t("forecast.made", { date: formatDate(createdAtUtc, language) })} {t("forecast.notAdvice")}{" "}
        <Link to="/how-forecasts-work" className="text-accent underline underline-offset-4">
          {t("forecast.howItWorks")}
        </Link>
      </p>
    </div>
  );
}
