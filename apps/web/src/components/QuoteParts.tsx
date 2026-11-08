import {
  changeDirection,
  formatAge,
  formatChangePct,
  formatDate,
  formatPrice,
} from "@marketlens/shared/format";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import type { Quote } from "../api/client";
import { useSettings } from "../settings/Settings";
import { useNow } from "./useNow";

/** How long a changed price shows its up/down colour (tokens.motion.priceFlashMs). */
const FLASH_MS = 600;

/**
 * A price that briefly takes the colour of its move when it changes, so a live update is noticed.
 * The flash is skipped for people who prefer reduced motion.
 */
export function Price({ quote, className = "" }: { quote: Quote; className?: string }) {
  const { language } = useSettings();
  const previous = useRef(quote.price);
  const [flash, setFlash] = useState<"up" | "down" | null>(null);

  useEffect(() => {
    const before = Number(previous.current);
    const after = Number(quote.price);
    previous.current = quote.price;
    if (before === after || window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
      return;
    }

    setFlash(after > before ? "up" : "down");
    const timer = window.setTimeout(() => {
      setFlash(null);
    }, FLASH_MS);
    return () => {
      window.clearTimeout(timer);
    };
  }, [quote.price]);

  const flashClass = flash === "up" ? "bg-up/15" : flash === "down" ? "bg-down/15" : "";
  return (
    <span
      className={`tabular rounded-control px-1 transition-colors duration-300 ${flashClass} ${className}`}
    >
      {formatPrice(quote.price, language)}
    </span>
  );
}

/** The change in percent, with ▲/▼ and a spoken direction, so colour never carries meaning alone. */
export function Change({ quote, withBasis = false }: { quote: Quote; withBasis?: boolean }) {
  const { t } = useTranslation();
  const { language } = useSettings();
  if (quote.changePct === null) {
    return null;
  }

  const direction = changeDirection(quote.changePct);
  const formatted = formatChangePct(quote.changePct, language);
  const arrow = direction === "up" ? "▲" : direction === "down" ? "▼" : "";
  const color = direction === "up" ? "text-up" : direction === "down" ? "text-down" : "text-muted";
  const spoken =
    direction === "flat" ? t("change.flat") : t(`change.${direction}`, { value: formatted });

  return (
    <span className={`tabular whitespace-nowrap ${color}`}>
      <span aria-hidden>
        {arrow} {formatted}
      </span>
      <span className="sr-only">{spoken}</span>
      {withBasis && (
        <span className="ms-2 text-muted">
          {quote.changeBasis === "rolling-24h" ? t("change.rolling24h") : t("change.previousClose")}
        </span>
      )}
    </span>
  );
}

/**
 * Says how current a price is (R13). A live coin shown in a currency with daily rates is "Live, with
 * the EUR rate of 8 Nov": the conversion is as old as its slower leg, which isn't the same as stale.
 */
export function Freshness({ quote, assetClass }: { quote: Quote; assetClass: string }) {
  const { t } = useTranslation();
  const { language } = useSettings();
  const now = useNow();

  if (quote.source === "sample") {
    return <span className="text-muted">{t("freshness.sample")}</span>;
  }

  if (quote.freshness === "daily") {
    const date = formatDate(quote.asOfUtc, language);
    const isLiveAsset = assetClass !== "fiat";
    return (
      <span className="text-muted">
        {isLiveAsset
          ? t("freshness.liveWithDailyRate", { currency: quote.in.replace("fiat:", ""), date })
          : t("freshness.rateOf", { date })}
      </span>
    );
  }

  return (
    <span className="text-muted">
      <span className={quote.freshness === "live" ? "font-semibold text-up" : ""}>
        {t(`freshness.${quote.freshness}`)}
      </span>{" "}
      {t("freshness.updated", { age: formatAge(quote.asOfUtc, now, language) })}
    </span>
  );
}
