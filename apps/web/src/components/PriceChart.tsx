import { formatPrice, type Language } from "@marketlens/shared/format";
import { useTranslation } from "react-i18next";
import {
  Area,
  AreaChart,
  CartesianGrid,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";

import type { Asset } from "../api/client";
import { useHistory, type ChartRange } from "../api/queries";
import { useSettings } from "../settings/Settings";
import { LoadError } from "./States";

interface ChartPoint {
  time: number;
  close: number;
}

const intradayRanges: readonly ChartRange[] = ["1D", "1W"];

/**
 * The price over a range, in the display currency (history from the API, already converted). A
 * quiet line over a faint fill; the forecast band joins it in M4. Screen readers get a sentence
 * with the first and last price instead of the drawing.
 */
export function PriceChart({
  asset,
  range,
  displayIn,
}: {
  asset: Asset;
  range: ChartRange;
  displayIn: string;
}) {
  const { t } = useTranslation();
  const { language } = useSettings();
  const history = useHistory(asset.key, range, displayIn);

  if (history.isError) {
    return (
      <div className="mt-4">
        <LoadError onRetry={() => void history.refetch()} />
      </div>
    );
  }

  if (history.isPending) {
    return <div aria-hidden className="mt-4 h-80 animate-pulse rounded-panel bg-surface" />;
  }

  const points: ChartPoint[] = history.data.points.map((point) => ({
    time: new Date(point.t).getTime(),
    close: Number(point.close),
  }));
  const isDailyFallback = history.data.interval === "1d" && intradayRanges.includes(range);
  const first = history.data.points[0];
  const last = history.data.points.at(-1);
  const currency = displayIn.split(":")[1] ?? displayIn;

  if (points.length === 0 || !first || !last) {
    return (
      <p className="mt-4 rounded-panel bg-surface p-6 text-small text-muted">
        {t("asset.chartEmpty")}
      </p>
    );
  }

  const summary = `${t("asset.chartLabel", { name: asset.name, range: t(`asset.ranges.${range}`) })}: ${formatPrice(first.close, language)} → ${formatPrice(last.close, language)} ${currency}`;

  return (
    <figure className="mt-4">
      <div role="img" aria-label={summary} className="h-80 rounded-panel bg-surface p-2 sm:p-4">
        <ResponsiveContainer width="100%" height="100%">
          <AreaChart data={points} margin={{ top: 8, right: 4, bottom: 0, left: 4 }}>
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
              dataKey="close"
              orientation="right"
              domain={["auto", "auto"]}
              tickFormatter={(close: number) => formatAxisPrice(close, language)}
              tick={{ fill: "var(--ml-color-muted-ink)", fontSize: 12 }}
              axisLine={false}
              tickLine={false}
              width={72}
            />
            <Tooltip
              formatter={(close) => [`${formatPrice(String(close), language)} ${currency}`, ""]}
              labelFormatter={(time) => formatTooltipTime(Number(time), range, language)}
              contentStyle={{
                background: "var(--ml-color-surface)",
                border: "1px solid var(--ml-color-divider)",
                borderRadius: "var(--ml-radius-control)",
                color: "var(--ml-color-ink)",
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
          </AreaChart>
        </ResponsiveContainer>
      </div>
      {isDailyFallback && (
        <figcaption className="mt-2 text-small text-muted">{t("asset.dailyOnly")}</figcaption>
      )}
    </figure>
  );
}

/** Axis labels stay short: 85K, 1.12, 0.00045. */
function formatAxisPrice(close: number, language: Language): string {
  return new Intl.NumberFormat(language, {
    notation: "compact",
    maximumSignificantDigits: 4,
  }).format(close);
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
