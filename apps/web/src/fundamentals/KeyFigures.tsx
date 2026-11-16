import {
  formatCompact,
  formatDate,
  formatPercent,
  formatPrice,
  type Language,
} from "@marketlens/shared/format";
import { useTranslation } from "react-i18next";
import {
  Bar,
  BarChart,
  CartesianGrid,
  ReferenceLine,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";

import type { Fundamentals } from "../api/client";
import { useFundamentals } from "../api/queries";
import { LoadError } from "../components/States";
import { useSettings } from "../settings/Settings";

type FinancialYear = Fundamentals["financialYears"][number];
type AnalystTrend = Fundamentals["analystTrends"][number];

const ANALYST_CATEGORIES = ["strongBuy", "buy", "hold", "sell", "strongSell"] as const;

/**
 * A stock's key figures (roadmap-and-fundamentals): market cap and ratios from Finnhub, five fiscal
 * years of revenue and net income from SEC filings, and the analyst counts Finnhub reports, each
 * with its source and the not-advice line. Every figure was computed and converted by the API;
 * this only formats them. Rendered for stocks only.
 */
export function KeyFigures({
  assetKey,
  assetName,
  displayIn,
}: {
  assetKey: string;
  assetName: string;
  displayIn: string;
}) {
  const { t } = useTranslation();
  const { language } = useSettings();
  const fundamentals = useFundamentals(assetKey, displayIn);

  return (
    <section aria-labelledby="key-figures-title" className="mt-10">
      <h2 id="key-figures-title" className="text-title font-semibold">
        {t("keyFigures.title")}
      </h2>
      {fundamentals.isPending && <KeyFiguresSkeleton />}
      {fundamentals.isError && (
        <div className="mt-3">
          <LoadError error={fundamentals.error} onRetry={() => void fundamentals.refetch()} />
        </div>
      )}
      {fundamentals.isSuccess && (
        <KeyFiguresBody
          fundamentals={fundamentals.data}
          assetName={assetName}
          language={language}
        />
      )}
    </section>
  );
}

function KeyFiguresBody({
  fundamentals,
  assetName,
  language,
}: {
  fundamentals: Fundamentals;
  assetName: string;
  language: Language;
}) {
  const { t } = useTranslation();
  const currency = fundamentals.in.split(":")[1] ?? fundamentals.in;

  if (fundamentals.status === "pending") {
    return (
      <p className="mt-3 text-small text-muted">{t("keyFigures.pending", { name: assetName })}</p>
    );
  }

  if (fundamentals.status !== "available" || !fundamentals.figures || !fundamentals.profile) {
    return <p className="mt-3 text-small text-muted">{t("keyFigures.noProviderKey")}</p>;
  }

  const { figures, profile } = fundamentals;
  const money = (value: string | null) =>
    value === null ? null : `${formatCompact(value, language)} ${currency}`;
  const price = (value: string | null) =>
    value === null ? null : `${formatPrice(value, language)} ${currency}`;
  const range52Week =
    figures.low52Week !== null && figures.high52Week !== null
      ? t("keyFigures.range52WeekValue", {
          low: price(figures.low52Week),
          high: price(figures.high52Week),
        })
      : null;

  const rows: { label: string; value: string | null; isLink?: boolean }[] = [
    { label: t("keyFigures.marketCap"), value: money(figures.marketCap) },
    {
      label: t("keyFigures.sharesOutstanding"),
      value:
        figures.sharesOutstanding === null
          ? null
          : formatCompact(figures.sharesOutstanding, language),
    },
    {
      label: t("keyFigures.peRatio"),
      value: figures.peRatio === null ? null : formatPrice(figures.peRatio, language),
    },
    { label: t("keyFigures.epsTtm"), value: price(figures.epsTtm) },
    {
      label: t("keyFigures.dividendYield"),
      value:
        figures.dividendYieldPct === null
          ? null
          : formatPercent(figures.dividendYieldPct, language, 2),
    },
    { label: t("keyFigures.range52Week"), value: range52Week },
    {
      label: t("keyFigures.beta"),
      value: figures.beta === null ? null : formatPrice(figures.beta, language),
    },
    {
      label: t("keyFigures.nextEarnings"),
      value:
        figures.nextEarningsDateUtc === null
          ? null
          : formatDate(figures.nextEarningsDateUtc, language),
    },
    { label: t("keyFigures.industry"), value: profile.industry },
    { label: t("keyFigures.exchange"), value: profile.exchange },
    {
      label: t("keyFigures.ipoDate"),
      value: profile.ipoDateUtc === null ? null : formatDate(profile.ipoDateUtc, language),
    },
    { label: t("keyFigures.website"), value: profile.websiteUrl, isLink: true },
  ];

  return (
    <div className="mt-3 space-y-8">
      <dl className="grid gap-x-8 rounded-panel bg-surface px-5 py-2 sm:grid-cols-2">
        {rows.map((row) => (
          <div
            key={row.label}
            className="flex items-baseline justify-between gap-4 border-b border-divider py-3 last:border-b-0 sm:[&:nth-last-child(2)]:border-b-0"
          >
            <dt className="text-small text-muted">{row.label}</dt>
            <dd className="min-w-0 text-end text-small font-semibold tabular-nums">
              {row.value === null ? (
                <span className="font-normal text-muted">{t("keyFigures.missing")}</span>
              ) : row.isLink ? (
                <a
                  href={row.value}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="break-all text-accent underline underline-offset-4"
                >
                  {new URL(row.value).hostname}
                </a>
              ) : (
                <span className="break-words">{row.value}</span>
              )}
            </dd>
          </div>
        ))}
      </dl>
      <p className="-mt-6 text-caption text-muted">{t("keyFigures.ttmNote")}</p>

      <FinancialYears years={fundamentals.financialYears} currency={currency} language={language} />
      <AnalystTrends
        trends={fundamentals.analystTrends}
        assetName={assetName}
        language={language}
      />

      <p className="text-caption text-muted">
        {fundamentals.asOfUtc &&
          t(
            fundamentals.sources.includes("sec-edgar")
              ? "keyFigures.sourcesWithFilings"
              : "keyFigures.sources",
            { date: formatDate(fundamentals.asOfUtc, language) },
          )}{" "}
        {fundamentals.in !== "fiat:USD" && t("keyFigures.currencyNote", { currency })}
      </p>
    </div>
  );
}

/** Revenue and net income per fiscal year, oldest first; a loss draws below zero. */
function FinancialYears({
  years,
  currency,
  language,
}: {
  years: FinancialYear[];
  currency: string;
  language: Language;
}) {
  const { t } = useTranslation();

  if (years.length === 0) {
    return (
      <div>
        <h3 className="text-body font-semibold">{t("keyFigures.yearsTitle")}</h3>
        <p className="mt-2 text-small text-muted">{t("keyFigures.yearsNone")}</p>
      </div>
    );
  }

  const label = (year: FinancialYear) =>
    t("keyFigures.fiscalYear", { year: new Date(year.periodEndUtc).getUTCFullYear() });
  const amount = (value: string | null) =>
    value === null ? t("keyFigures.missing") : `${formatCompact(value, language)} ${currency}`;
  const oldestFirst = [...years].reverse();
  const bars = oldestFirst.map((year) => ({
    label: label(year),
    revenue: Number(year.revenue),
    netIncome: year.netIncome === null ? undefined : Number(year.netIncome),
  }));
  const summary = t("keyFigures.yearsSummary", {
    count: years.length,
    currency,
    years: oldestFirst
      .map((year) =>
        t("keyFigures.yearSummary", {
          label: label(year),
          revenue: amount(year.revenue),
          netIncome: amount(year.netIncome),
        }),
      )
      .join("; "),
  });

  return (
    <div>
      <h3 className="text-body font-semibold">{t("keyFigures.yearsTitle")}</h3>
      <div
        role="img"
        aria-label={summary}
        className="mt-3 h-64 rounded-panel bg-surface p-2 sm:p-4"
      >
        <ResponsiveContainer width="100%" height="100%">
          <BarChart
            data={bars}
            margin={{ top: 8, right: 4, bottom: 0, left: 4 }}
            accessibilityLayer={false}
          >
            <CartesianGrid stroke="var(--ml-color-divider)" vertical={false} />
            <XAxis
              dataKey="label"
              tick={{ fill: "var(--ml-color-muted-ink)", fontSize: 12 }}
              axisLine={false}
              tickLine={false}
            />
            <YAxis
              orientation="right"
              tickFormatter={(value: number) => formatCompact(String(value), language)}
              tick={{ fill: "var(--ml-color-muted-ink)", fontSize: 12 }}
              axisLine={false}
              tickLine={false}
              width={64}
            />
            <ReferenceLine y={0} stroke="var(--ml-color-muted-ink)" />
            <Tooltip
              cursor={{ fill: "var(--ml-color-background)" }}
              formatter={(value) =>
                typeof value === "number"
                  ? `${formatCompact(String(value), language)} ${currency}`
                  : ""
              }
              contentStyle={{
                background: "var(--ml-color-surface)",
                border: "1px solid var(--ml-color-divider)",
                borderRadius: 8,
                fontSize: 14,
              }}
            />
            <Bar
              dataKey="revenue"
              name={t("keyFigures.revenue")}
              fill="var(--ml-color-accent)"
              radius={[3, 3, 0, 0]}
              isAnimationActive={false}
            />
            <Bar
              dataKey="netIncome"
              name={t("keyFigures.netIncome")}
              fill="var(--ml-color-ink)"
              fillOpacity={0.55}
              radius={[3, 3, 0, 0]}
              isAnimationActive={false}
            />
          </BarChart>
        </ResponsiveContainer>
      </div>
      <ul aria-hidden className="mt-2 flex flex-wrap gap-x-5 text-caption text-muted">
        <li className="flex items-center gap-2">
          <span className="size-3 rounded-sm bg-accent" />
          {t("keyFigures.revenue")}
        </li>
        <li className="flex items-center gap-2">
          <span className="size-3 rounded-sm bg-ink/55" />
          {t("keyFigures.netIncome")}
        </li>
      </ul>
      {/* A table ignores the 1 px width of sr-only and would widen the page; its wrapper doesn't. */}
      <div className="sr-only">
        <table>
          <caption>{t("keyFigures.yearsTitle")}</caption>
          <thead>
            <tr>
              <th scope="col">{t("keyFigures.fiscalYearHeader")}</th>
              <th scope="col">{t("keyFigures.revenue")}</th>
              <th scope="col">{t("keyFigures.netIncome")}</th>
            </tr>
          </thead>
          <tbody>
            {years.map((year) => (
              <tr key={year.periodEndUtc}>
                <th scope="row">
                  {t("keyFigures.fiscalYearEnded", {
                    date: formatDate(year.periodEndUtc, language),
                  })}
                </th>
                <td>{amount(year.revenue)}</td>
                <td>{amount(year.netIncome)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

/** Analyst counts per month, newest first, with who reported them and that they aren't advice (D10). */
function AnalystTrends({
  trends,
  assetName,
  language,
}: {
  trends: AnalystTrend[];
  assetName: string;
  language: Language;
}) {
  const { t } = useTranslation();
  const monthFormat = new Intl.DateTimeFormat(language, {
    month: "long",
    year: "numeric",
    timeZone: "UTC",
  });

  return (
    <div>
      <h3 className="text-body font-semibold">{t("keyFigures.analystsTitle")}</h3>
      <p className="mt-1 max-w-prose text-small text-muted">
        {t("keyFigures.analystsIntro", { name: assetName })}
      </p>
      {trends.length === 0 ? (
        <p className="mt-2 text-small text-muted">{t("keyFigures.analystsNone")}</p>
      ) : (
        <div className="mt-3 overflow-x-auto rounded-panel bg-surface">
          <table className="w-full text-small">
            <caption className="sr-only">{t("keyFigures.analystsTitle")}</caption>
            <thead>
              <tr className="text-muted">
                <th scope="col" className="px-4 py-2 text-start font-normal">
                  {t("keyFigures.month")}
                </th>
                {ANALYST_CATEGORIES.map((category) => (
                  <th key={category} scope="col" className="px-3 py-2 text-end font-normal">
                    {t(`keyFigures.${category}`)}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-divider border-t border-divider">
              {trends.map((trend) => (
                <tr key={trend.periodUtc}>
                  <th scope="row" className="whitespace-nowrap px-4 py-2 text-start font-normal">
                    {monthFormat.format(new Date(trend.periodUtc))}
                  </th>
                  {ANALYST_CATEGORIES.map((category) => (
                    <td key={category} className="px-3 py-2 text-end tabular-nums">
                      {trend[category]}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

/** Shaped like the figures grid, so nothing jumps when they arrive. */
function KeyFiguresSkeleton() {
  return (
    <div
      aria-hidden
      className="mt-3 grid gap-x-8 rounded-panel bg-surface px-5 py-2 sm:grid-cols-2"
    >
      {Array.from({ length: 8 }, (_, row) => (
        <div key={row} className="flex justify-between gap-4 py-3">
          <span className="h-4 w-28 animate-pulse rounded-control bg-background" />
          <span className="h-4 w-20 animate-pulse rounded-control bg-background" />
        </div>
      ))}
    </div>
  );
}
