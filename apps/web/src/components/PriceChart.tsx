import { formatChangePct, formatDate, formatPrice, type Language } from "@marketlens/shared/format";
import { useId, useState, type KeyboardEvent, type PointerEvent } from "react";
import { useTranslation } from "react-i18next";
import {
  Area,
  CartesianGrid,
  ComposedChart,
  Line,
  ReferenceArea,
  ReferenceLine,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";

import type { Asset, Forecast, History } from "../api/client";
import { useHistory, type ChartRange } from "../api/queries";
import { useSettings } from "../settings/Settings";
import { ChangePct } from "./QuoteParts";
import { LoadError } from "./States";

export interface ChartPoint {
  time: number;
  close?: number;
  median?: number;
  band80?: [number, number];
  band95?: [number, number];
}

const intradayRanges: readonly ChartRange[] = ["1D", "1W"];
const DAILY_INTERVAL = "1d";

/** The plot sits inside these margins and left of the price axis; pointer positions map through them. */
const CHART_MARGIN = { top: 8, right: 4, bottom: 0, left: 4 } as const;
const PRICE_AXIS_WIDTH = 72;

/** Percent changes keep four decimals before display rounds them to two. */
const PERCENT_DECIMALS = 4;

/**
 * Two past points picked on one chart (D58), stored by time so a refetch that shifts the points
 * keeps them; `scope` ties them to the asset, range and display currency they were picked on.
 */
interface Comparison {
  scope: string;
  startTime: number;
  endTime: number;
}

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
 * The index of the past close nearest to a time, clamped to the history so a point is never picked
 * among the forecast days drawn after the last close. -1 for an empty history.
 */
export function nearestHistoryIndex(history: History["points"], time: number): number {
  let nearestIndex = -1;
  let nearestDistance = Infinity;
  history.forEach((point, index) => {
    const distance = Math.abs(Date.parse(point.t) - time);
    if (distance < nearestDistance) {
      nearestIndex = index;
      nearestDistance = distance;
    }
  });
  return nearestIndex;
}

/**
 * The change from one close to another, computed exactly on the API's decimal strings (never on
 * the numbers drawn): the difference in the same currency, and the percent change at four decimals,
 * rounded half away from zero. Both points are prices the API served; nothing here is stored.
 */
export function compareCloses(
  startClose: string,
  endClose: string,
): { difference: string; changePct: string } {
  const start = parseDecimal(startClose);
  const end = parseDecimal(endClose);
  const scale = Math.max(start.scale, end.scale);
  const startUnits = start.units * 10n ** BigInt(scale - start.scale);
  const endUnits = end.units * 10n ** BigInt(scale - end.scale);
  if (startUnits <= 0n) {
    throw new RangeError(`A start price must be positive, got ${startClose}.`);
  }

  const differenceUnits = endUnits - startUnits;
  const percentNumerator = differenceUnits * 100n * 10n ** BigInt(PERCENT_DECIMALS);
  const percentUnits = divideRoundingHalfAwayFromZero(percentNumerator, startUnits);
  return {
    difference: formatUnits(differenceUnits, scale),
    changePct: formatUnits(percentUnits, PERCENT_DECIMALS),
  };
}

function parseDecimal(value: string): { units: bigint; scale: number } {
  const match = /^(-?)(\d+)(?:\.(\d+))?$/.exec(value.trim());
  if (!match) {
    throw new RangeError(`Not a decimal price: ${value}.`);
  }

  const [, sign = "", whole = "0", fraction = ""] = match;
  return { units: BigInt(`${sign}${whole}${fraction}`), scale: fraction.length };
}

function divideRoundingHalfAwayFromZero(numerator: bigint, denominator: bigint): bigint {
  const quotient = numerator / denominator;
  const remainder = numerator % denominator;
  const isHalfOrMore = 2n * (remainder < 0n ? -remainder : remainder) >= denominator;
  if (!isHalfOrMore) {
    return quotient;
  }

  return numerator < 0n ? quotient - 1n : quotient + 1n;
}

/** Units at a scale as a plain decimal string without trailing zeros: 75034 at 2 → "750.34". */
function formatUnits(units: bigint, scale: number): string {
  const isNegative = units < 0n;
  const digits = (isNegative ? -units : units).toString().padStart(scale + 1, "0");
  const whole = digits.slice(0, digits.length - scale);
  const fraction = digits.slice(digits.length - scale).replace(/0+$/, "");
  const magnitude = fraction === "" ? whole : `${whole}.${fraction}`;
  return isNegative && magnitude !== "0" ? `-${magnitude}` : magnitude;
}

/**
 * The price over a range, in the display currency (history from the API, already converted). A
 * quiet line over a faint fill, handing over to the forecast range when one is passed: the 80%
 * band inside a fainter 95% band, with the middle estimate dashed. Screen readers get a sentence
 * with the first and last price, and the forecast's last day, instead of the drawing.
 *
 * Dragging across the chart compares two past points (D58): the shaded span, both prices, the
 * difference and the percent change. Two sliders under the chart do the same from the keyboard
 * and with a screen reader (D21).
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
  const [comparison, setComparison] = useState<Comparison | null>(null);
  const [dragAnchorTime, setDragAnchorTime] = useState<number | null>(null);
  const [announcement, setAnnouncement] = useState("");

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
  const interval = history.data.interval;
  const isDailyFallback = interval === DAILY_INTERVAL && intradayRanges.includes(range);
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
  const pastPoints = history.data.points;
  const scope = `${asset.key}|${range}|${displayIn}`;
  // A comparison picked on another asset, range or currency doesn't carry over.
  const picked = comparison?.scope === scope ? comparison : null;
  const startIndex = picked ? nearestHistoryIndex(pastPoints, picked.startTime) : 0;
  const endIndex = picked ? nearestHistoryIndex(pastPoints, picked.endTime) : pastPoints.length - 1;
  const canCompare = pastPoints.length > 1;
  const firstTime = points[0]?.time ?? handOverTime;
  const lastTime = points.at(-1)?.time ?? handOverTime;
  const pointValue = (index: number) => {
    const point = pastPoints[index] ?? last;
    return t("asset.compare.pointValue", {
      time: formatPointTime(Date.parse(point.t), interval, language),
      price: formatPrice(point.close, language),
      currency,
    });
  };

  const pick = (startTime: number, endTime: number) => {
    setComparison({ scope, startTime, endTime });
  };

  const clear = () => {
    if (picked) {
      setAnnouncement(t("asset.compare.cleared"));
    }

    setComparison(null);
    setDragAnchorTime(null);
  };

  const announce = (startTime: number, endTime: number) => {
    const earlier = pastPoints[nearestHistoryIndex(pastPoints, Math.min(startTime, endTime))];
    const later = pastPoints[nearestHistoryIndex(pastPoints, Math.max(startTime, endTime))];
    if (earlier && later && earlier !== later) {
      setAnnouncement(comparisonSentence(earlier, later, interval, currency, language, t));
    }
  };

  // The pointer's x position as the time of the nearest past close.
  const pastTimeAt = (event: PointerEvent<HTMLDivElement>) => {
    const bounds = event.currentTarget.getBoundingClientRect();
    const plotLeft = bounds.left + CHART_MARGIN.left;
    const plotWidth = bounds.width - CHART_MARGIN.left - CHART_MARGIN.right - PRICE_AXIS_WIDTH;
    const share = Math.min(1, Math.max(0, (event.clientX - plotLeft) / plotWidth));
    const index = nearestHistoryIndex(pastPoints, firstTime + share * (lastTime - firstTime));
    return Date.parse((pastPoints[index] ?? last).t);
  };

  const startDrag = (event: PointerEvent<HTMLDivElement>) => {
    if (!canCompare || !event.isPrimary || event.button !== 0) {
      return;
    }

    // Capture keeps the drag going when the pointer leaves the chart before it is released.
    event.currentTarget.setPointerCapture(event.pointerId);
    setDragAnchorTime(pastTimeAt(event));
  };

  const moveDrag = (event: PointerEvent<HTMLDivElement>) => {
    if (dragAnchorTime === null) {
      return;
    }

    const time = pastTimeAt(event);
    if (time !== dragAnchorTime) {
      pick(dragAnchorTime, time);
    }
  };

  const endDrag = (event: PointerEvent<HTMLDivElement>) => {
    if (dragAnchorTime === null) {
      return;
    }

    const endTime = pastTimeAt(event);
    setDragAnchorTime(null);
    // A press without a drag clears the comparison, like clicking outside a text selection.
    if (endTime === dragAnchorTime) {
      clear();
      return;
    }

    pick(dragAnchorTime, endTime);
    announce(dragAnchorTime, endTime);
  };

  const pickBySlider = (which: "start" | "end", index: number) => {
    const timeOf = (pointIndex: number) => Date.parse((pastPoints[pointIndex] ?? last).t);
    const startTime = which === "start" ? timeOf(index) : timeOf(startIndex);
    const endTime = which === "end" ? timeOf(index) : timeOf(endIndex);
    pick(startTime, endTime);
    announce(startTime, endTime);
  };

  const clearOnEscape = (event: KeyboardEvent<HTMLElement>) => {
    if (event.key === "Escape" && picked) {
      event.preventDefault();
      clear();
    }
  };

  return (
    <figure className="mt-4">
      <div
        role="img"
        aria-label={summary}
        className="h-80 touch-pan-y select-none rounded-panel bg-surface p-2 sm:p-4"
      >
        <div
          className="h-full"
          onPointerDown={startDrag}
          onPointerMove={moveDrag}
          onPointerUp={endDrag}
          onPointerCancel={() => {
            setDragAnchorTime(null);
          }}
        >
          <ResponsiveContainer width="100%" height="100%">
            <ComposedChart data={points} margin={CHART_MARGIN} accessibilityLayer={false}>
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
                width={PRICE_AXIS_WIDTH}
              />
              <Tooltip
                content={({ active, payload }) => {
                  const hovered: unknown = payload[0]?.payload;
                  return active && isChartPoint(hovered) ? (
                    <ChartTooltip point={hovered} interval={interval} currency={currency} />
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
              {picked && picked.startTime !== picked.endTime && (
                <ReferenceArea
                  x1={Math.min(picked.startTime, picked.endTime)}
                  x2={Math.max(picked.startTime, picked.endTime)}
                  fill="var(--ml-color-accent)"
                  fillOpacity={0.1}
                  stroke="var(--ml-color-accent)"
                  strokeOpacity={0.6}
                  ifOverflow="hidden"
                />
              )}
            </ComposedChart>
          </ResponsiveContainer>
        </div>
      </div>
      {hasForecast && <ForecastLegend />}
      {isDailyFallback && (
        <figcaption className="mt-2 text-small text-muted">{t("asset.dailyOnly")}</figcaption>
      )}
      {canCompare && (
        <ComparisonControls
          pointCount={pastPoints.length}
          startIndex={startIndex}
          endIndex={endIndex}
          pointValue={pointValue}
          onPick={pickBySlider}
          onKeyDown={clearOnEscape}
        />
      )}
      {canCompare && (
        <ComparisonSummary
          start={picked ? pastPoints[Math.min(startIndex, endIndex)] : undefined}
          end={picked ? pastPoints[Math.max(startIndex, endIndex)] : undefined}
          interval={interval}
          currency={currency}
          onClear={clear}
        />
      )}
      <p aria-live="polite" className="sr-only">
        {announcement}
      </p>
    </figure>
  );
}

/**
 * The keyboard and screen-reader way to compare (D21): native sliders over the past closes, so
 * arrows, Home, End and Page keys work as people expect, and each value reads "time, price".
 */
function ComparisonControls({
  pointCount,
  startIndex,
  endIndex,
  pointValue,
  onPick,
  onKeyDown,
}: {
  pointCount: number;
  startIndex: number;
  endIndex: number;
  pointValue: (index: number) => string;
  onPick: (which: "start" | "end", index: number) => void;
  onKeyDown: (event: KeyboardEvent<HTMLElement>) => void;
}) {
  const { t } = useTranslation();
  const id = useId();
  const sliders = [
    { which: "start", label: t("asset.compare.start"), index: startIndex },
    { which: "end", label: t("asset.compare.end"), index: endIndex },
  ] as const;

  return (
    <fieldset className="mt-3 grid gap-x-4 gap-y-1 sm:grid-cols-2">
      <legend className="sr-only">{t("asset.compare.title")}</legend>
      {sliders.map((slider) => (
        <label
          key={slider.which}
          htmlFor={`${id}-${slider.which}`}
          className="flex min-h-11 items-center gap-3 text-small text-muted"
        >
          <span className="w-24 shrink-0">{slider.label}</span>
          <input
            id={`${id}-${slider.which}`}
            type="range"
            min={0}
            max={pointCount - 1}
            step={1}
            value={slider.index}
            aria-valuetext={pointValue(slider.index)}
            onChange={(event) => {
              onPick(slider.which, Number(event.target.value));
            }}
            onKeyDown={onKeyDown}
            className="w-full accent-accent"
          />
        </label>
      ))}
    </fieldset>
  );
}

/** Both prices, the difference and the percent change, earlier point first; a hint until then. */
function ComparisonSummary({
  start,
  end,
  interval,
  currency,
  onClear,
}: {
  start: History["points"][number] | undefined;
  end: History["points"][number] | undefined;
  interval: string;
  currency: string;
  onClear: () => void;
}) {
  const { t } = useTranslation();
  const { language } = useSettings();

  if (!start || !end || start === end) {
    return <p className="mt-2 text-small text-muted">{t("asset.compare.hint")}</p>;
  }

  const { difference, changePct } = compareCloses(start.close, end.close);
  const signedDifference = `${Number(difference) > 0 ? "+" : ""}${formatPrice(difference, language)}`;
  const point = (close: History["points"][number]) => (
    <span className="whitespace-nowrap">
      <span className="text-muted">{formatPointTime(Date.parse(close.t), interval, language)}</span>{" "}
      <span className="font-semibold tabular-nums">
        {formatPrice(close.close, language)} {currency}
      </span>
    </span>
  );

  return (
    <div className="mt-2 flex flex-wrap items-center justify-between gap-3 rounded-panel bg-surface px-4 py-3 text-small">
      <p className="flex flex-wrap items-center gap-x-2 gap-y-1">
        {point(start)}
        <span aria-hidden className="text-muted">
          →
        </span>
        <span className="sr-only">{t("asset.compare.to")}</span>
        {point(end)}
        <span className="flex items-center gap-2">
          <ChangePct changePct={changePct} />
          <span className="tabular-nums text-muted">
            ({signedDifference} {currency})
          </span>
        </span>
      </p>
      <button
        type="button"
        onClick={onClear}
        className="min-h-11 rounded-control border border-divider px-3 font-semibold hover:bg-background"
      >
        {t("asset.compare.clear")}
      </button>
    </div>
  );
}

function comparisonSentence(
  earlier: History["points"][number],
  later: History["points"][number],
  interval: string,
  currency: string,
  language: Language,
  t: ReturnType<typeof useTranslation>["t"],
): string {
  const { difference, changePct } = compareCloses(earlier.close, later.close);
  return t("asset.compare.summary", {
    startTime: formatPointTime(Date.parse(earlier.t), interval, language),
    startPrice: formatPrice(earlier.close, language),
    endTime: formatPointTime(Date.parse(later.t), interval, language),
    endPrice: formatPrice(later.close, language),
    change: formatChangePct(changePct, language),
    difference: `${Number(difference) > 0 ? "+" : ""}${formatPrice(difference, language)}`,
    currency,
  });
}

/** Recharts hands the hovered datum back untyped; it is one of the points this chart passed in. */
function isChartPoint(value: unknown): value is ChartPoint {
  return (
    typeof value === "object" && value !== null && "time" in value && typeof value.time === "number"
  );
}

function ChartTooltip({
  point,
  interval,
  currency,
}: {
  point: ChartPoint;
  interval: string;
  currency: string;
}) {
  const { t } = useTranslation();
  const { language } = useSettings();
  const price = (value: number) => `${formatPrice(String(value), language)} ${currency}`;

  return (
    <div className="rounded-control border border-divider bg-surface px-3 py-2 text-small shadow-sm">
      <p className="text-muted">{formatPointTime(point.time, interval, language)}</p>
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

/**
 * A point's time as the tooltip and the comparison show it: with the hour for minute and hour
 * points, the date alone for daily ones (daily rates on a 1-day chart have no meaningful hour).
 */
function formatPointTime(time: number, interval: string, language: Language): string {
  const options: Intl.DateTimeFormatOptions =
    interval === DAILY_INTERVAL
      ? { day: "numeric", month: "short", year: "numeric" }
      : { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" };
  return new Intl.DateTimeFormat(language, options).format(new Date(time));
}
