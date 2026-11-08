import { formatDate, formatPrice, type Language } from "@marketlens/shared/format";
import { useTranslation } from "react-i18next";
import {
  Area,
  CartesianGrid,
  ComposedChart,
  Line,
  ReferenceLine,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";

import type { Asset, Forecast, History } from "../api/client";
import { useHistory, type ChartRange } from "../api/queries";
import { useSettings } from "../settings/Settings";
import { LoadError } from "./States";

export interface ChartPoint {
  time: number;
  close?: number;
  median?: number;
  band80?: [number, number];
  band95?: [number, number];
}

const intradayRanges: readonly ChartRange[] = ["1D", "1W"];

/**
 * Ranges where a seven-day forecast reads well beside the history (D59): on 1D it would squeeze the
 * day into a corner, on 1Y and 5Y it would be a sliver.
 */
export const forecastRanges: readonly ChartRange[] = ["1W", "1M", "3M"];

/**
 * History points, then forecast points. The last close also opens the band, so the history line
 * hands over to the range instead of leaving a gap. Prices become numbers here for drawing only.
 */
export function toChartPoints(
  history: History["points"],
  forecastPoints: Forecast["points"],
): ChartPoint[] {
  const points: ChartPoint[] = history.map((point) => ({
    time: Date.parse(point.t),
    close: Number(point.close),
  }));
  const last = points.at(-1);
  if (last?.close === undefined || forecastPoints.length === 0) {
    return points;
  }

  const lastClose = last.close;
  points[points.length - 1] = {
    ...last,
    median: lastClose,
    band80: [lastClose, lastClose],
    band95: [lastClose, lastClose],
  };
  for (const point of forecastPoints) {
    const time = Date.parse(point.t);
    if (time > last.time) {
      points.push({
        time,
        median: Number(point.median),
        band80: [Number(point.low80), Number(point.high80)],
        band95: [Number(point.low95), Number(point.high95)],
      });
    }
  }

  return points;
}

/**
 * The price over a range, in the display currency (history from the API, already converted). A
 * quiet line over a faint fill, handing over to the forecast range when one is passed: the 80%
 * band inside a fainter 95% band, with the middle estimate dashed. Screen readers get a sentence
 * with the first and last price, and the forecast's last day, instead of the drawing.
 */
export function PriceChart({
  asset,
  range,
  displayIn,
  forecastPoints = [],
}: {
  asset: Asset;
  range: ChartRange;
  displayIn: string;
  forecastPoints?: Forecast["points"];
}) {
  const { t } = useTranslation();
  const { language } = useSettings();
  const history = useHistory(asset.key, range, displayIn);

  if (history.isError) {
    return (
      <div className="mt-4">
        <LoadError error={history.error} onRetry={() => void history.refetch()} />
      </div>
    );
  }

  if (history.isPending) {
    return <div aria-hidden className="mt-4 h-80 animate-pulse rounded-panel bg-surface" />;
  }

  const points = toChartPoints(history.data.points, forecastPoints);
  const isDailyFallback = history.data.interval === "1d" && intradayRanges.includes(range);
  const first = history.data.points[0];
  const last = history.data.points.at(-1);
  const lastForecast = forecastPoints.at(-1);
  const hasForecast = points.some((point) => point.close === undefined);
  const currency = displayIn.split(":")[1] ?? displayIn;

  if (points.length === 0 || !first || !last) {
    return (
      <p className="mt-4 rounded-panel bg-surface p-6 text-small text-muted">
        {t("asset.chartEmpty")}
      </p>
    );
  }

  const historySummary = `${t("asset.chartLabel", { name: asset.name, range: t(`asset.ranges.${range}`) })}: ${formatPrice(first.close, language)} → ${formatPrice(last.close, language)} ${currency}`;
  const summary =
    hasForecast && lastForecast
      ? `${historySummary}. ${t("forecast.chartSummary", {
          date: formatDate(lastForecast.t, language),
          low: formatPrice(lastForecast.low80, language),
          high: formatPrice(lastForecast.high80, language),
          currency,
        })}`
      : historySummary;
  const handOverTime = Date.parse(last.t);

  return (
    <figure className="mt-4">
      <div role="img" aria-label={summary} className="h-80 rounded-panel bg-surface p-2 sm:p-4">
        <ResponsiveContainer width="100%" height="100%">
          <ComposedChart data={points} margin={{ top: 8, right: 4, bottom: 0, left: 4 }}>
            <defs>
              <linearGradient id="price-fill" x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor="var(--ml-color-accent)" stopOpacity={0.14} />
                <stop offset="100%" stopColor="var(--ml-color-accent)" stopOpacity={0} />
              </linearGradient>
            </defs>
            <CartesianGrid stroke="var(--ml-color-divider)" vertical={false} />
            <XAxis
              dataKey="time"
              type="number"
              scale="time"
              domain={["dataMin", "dataMax"]}
              tickFormatter={(time: number) => formatTick(time, range, language)}
              tick={{ fill: "var(--ml-color-muted-ink)", fontSize: 12 }}
              axisLine={false}
              tickLine={false}
              minTickGap={40}
            />
            <YAxis
              orientation="right"
              domain={["auto", "auto"]}
              tickFormatter={(price: number) => formatAxisPrice(price, language)}
              tick={{ fill: "var(--ml-color-muted-ink)", fontSize: 12 }}
              axisLine={false}
              tickLine={false}
              width={72}
            />
            <Tooltip
              content={({ active, payload }) => {
                const hovered: unknown = payload[0]?.payload;
                return active && isChartPoint(hovered) ? (
                  <ChartTooltip point={hovered} range={range} currency={currency} />
                ) : null;
              }}
            />
            <Area
              type="monotone"
              dataKey="close"
              stroke="var(--ml-color-ink)"
              strokeWidth={1.75}
              fill="url(#price-fill)"
              isAnimationActive={false}
              dot={false}
            />
            {hasForecast && (
              <>
                <ReferenceLine x={handOverTime} stroke="var(--ml-color-divider)" />
                <Area
                  type="monotone"
                  dataKey="band95"
                  stroke="none"
                  fill="var(--ml-color-accent)"
                  fillOpacity={0.12}
                  isAnimationActive={false}
                  activeDot={false}
                />
                <Area
                  type="monotone"
                  dataKey="band80"
                  stroke="none"
                  fill="var(--ml-color-accent)"
                  fillOpacity={0.26}
                  isAnimationActive={false}
                  activeDot={false}
                />
                <Line
                  type="monotone"
                  dataKey="median"
                  stroke="var(--ml-color-accent)"
                  strokeWidth={1.5}
                  strokeDasharray="5 4"
                  dot={false}
                  isAnimationActive={false}
                />
              </>
            )}
          </ComposedChart>
        </ResponsiveContainer>
      </div>
      {hasForecast && <ForecastLegend />}
      {isDailyFallback && (
        <figcaption className="mt-2 text-small text-muted">{t("asset.dailyOnly")}</figcaption>
      )}
    </figure>
  );
}

/** Recharts hands the hovered datum back untyped; it is one of the points this chart passed in. */
function isChartPoint(value: unknown): value is ChartPoint {
  return (
    typeof value === "object" && value !== null && "time" in value && typeof value.time === "number"
  );
}

function ChartTooltip({
  point,
  range,
  currency,
}: {
  point: ChartPoint;
  range: ChartRange;
  currency: string;
}) {
  const { t } = useTranslation();
  const { language } = useSettings();
  const price = (value: number) => `${formatPrice(String(value), language)} ${currency}`;

  return (
    <div className="rounded-control border border-divider bg-surface px-3 py-2 text-small shadow-sm">
      <p className="text-muted">{formatTooltipTime(point.time, range, language)}</p>
      {point.close !== undefined ? (
        <p className="font-semibold tabular-nums">{price(point.close)}</p>
      ) : (
        point.median !== undefined &&
        point.band80 && (
          <>
            <p className="font-semibold tabular-nums">
              {t("forecast.tooltipMedian", { price: price(point.median) })}
            </p>
            <p className="tabular-nums">
              {t("forecast.tooltipRange", {
                low: price(point.band80[0]),
                high: price(point.band80[1]),
              })}
            </p>
          </>
        )
      )}
    </div>
  );
}

/** What the band's shades mean; the chart itself is hidden from screen readers behind its summary. */
function ForecastLegend() {
  const { t } = useTranslation();

  return (
    <ul aria-hidden className="mt-2 flex flex-wrap gap-x-5 gap-y-1 text-caption text-muted">
      <li className="flex items-center gap-2">
        <span className="h-3 w-4 rounded-sm border border-accent/50 bg-accent/25" />
        {t("forecast.legend80")}
      </li>
      <li className="flex items-center gap-2">
        <span className="h-3 w-4 rounded-sm border border-accent/30 bg-accent/10" />
        {t("forecast.legend95")}
      </li>
      <li className="flex items-center gap-2">
        <span className="w-4 border-t-2 border-dashed border-accent" />
        {t("forecast.legendMedian")}
      </li>
    </ul>
  );
}

/** Axis labels stay short: 85K, 1.12, 0.00045. */
function formatAxisPrice(price: number, language: Language): string {
  return new Intl.NumberFormat(language, {
    notation: "compact",
    maximumSignificantDigits: 4,
  }).format(price);
}

function formatTick(time: number, range: ChartRange, language: Language): string {
  const options: Intl.DateTimeFormatOptions =
    range === "1D"
      ? { hour: "2-digit", minute: "2-digit" }
      : range === "1Y" || range === "5Y"
        ? { month: "short", year: "2-digit" }
        : { day: "numeric", month: "short" };
  return new Intl.DateTimeFormat(language, options).format(new Date(time));
}

function formatTooltipTime(time: number, range: ChartRange, language: Language): string {
  const options: Intl.DateTimeFormatOptions = intradayRanges.includes(range)
    ? { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" }
    : { day: "numeric", month: "short", year: "numeric" };
  return new Intl.DateTimeFormat(language, options).format(new Date(time));
}
