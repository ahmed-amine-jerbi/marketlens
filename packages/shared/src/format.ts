/**
 * Number, change and time formatting for both apps, per language. Prices arrive from the API as
 * decimal strings (D39); they are turned into numbers here for display only, never for maths.
 */

export const supportedLanguages = ["en", "fr", "de"] as const;
export type Language = (typeof supportedLanguages)[number];

export function isLanguage(value: string | null | undefined): value is Language {
  return supportedLanguages.some((language) => language === value);
}

const MINUS_SIGN = "\u2212";

/**
 * A price with precision that suits its size: 87,412.55 / 1.0832 / 0.00001234. Large values get
 * two decimals; values from 1 to 1,000 up to four; below 1, four significant digits.
 */
export function formatPrice(value: string, language: Language): string {
  const amount = Number(value);
  if (!Number.isFinite(amount)) {
    return value;
  }

  const magnitude = Math.abs(amount);
  const options: Intl.NumberFormatOptions =
    magnitude >= 1000
      ? { minimumFractionDigits: 2, maximumFractionDigits: 2 }
      : magnitude >= 1
        ? { minimumFractionDigits: 2, maximumFractionDigits: 4 }
        : { minimumSignificantDigits: 2, maximumSignificantDigits: 4 };

  return useTypographicMinus(new Intl.NumberFormat(language, options).format(amount));
}

/** A change in percent with its sign and a true minus sign (+1.80 %, -0.82 %; spacing per language). Null stays empty. */
export function formatChangePct(changePct: string | null, language: Language): string {
  if (changePct === null) {
    return "";
  }

  const fraction = Number(changePct) / 100;
  const formatted = new Intl.NumberFormat(language, {
    style: "percent",
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
    signDisplay: "exceptZero",
  }).format(fraction);
  return useTypographicMinus(formatted);
}

/** Whether a change is up, down or flat, so callers pair colour with ▲/▼ and never use colour alone. */
export function changeDirection(changePct: string | null): "up" | "down" | "flat" {
  const change = Number(changePct ?? 0);
  return change > 0 ? "up" : change < 0 ? "down" : "flat";
}

/** How long ago a price was observed: "2 sec. ago", "il y a 5 min", "vor 3 Std.". */
export function formatAge(asOfUtc: string, now: Date, language: Language): string {
  const seconds = Math.max(0, Math.round((now.getTime() - new Date(asOfUtc).getTime()) / 1000));
  const relative = new Intl.RelativeTimeFormat(language, { numeric: "auto", style: "short" });

  if (seconds < 60) {
    return relative.format(-seconds, "second");
  }

  const minutes = Math.round(seconds / 60);
  if (minutes < 60) {
    return relative.format(-minutes, "minute");
  }

  const hours = Math.round(minutes / 60);
  if (hours < 48) {
    return relative.format(-hours, "hour");
  }

  return relative.format(-Math.round(hours / 24), "day");
}

/** A calendar date in the language's style: "8 Nov 2026", "8 nov. 2026", "8. Nov. 2026". */
export function formatDate(isoDate: string, language: Language): string {
  return new Intl.DateTimeFormat(language, {
    day: "numeric",
    month: "short",
    year: "numeric",
    timeZone: "UTC",
  }).format(new Date(isoDate));
}

/**
 * Reads an amount typed in the user's language ("0,25" in French, "1.234,5" in German) into an
 * exact decimal string for the API ("0.25", "1234.5"). Returns null when it isn't a positive number.
 */
export function parseAmount(text: string, language: Language): string | null {
  const parts = new Intl.NumberFormat(language).formatToParts(12345.6);
  const groupSeparator = parts.find((part) => part.type === "group")?.value ?? ",";
  const decimalSeparator = parts.find((part) => part.type === "decimal")?.value ?? ".";

  const withoutGroups = text
    .trim()
    .replace(/[\s\u00A0\u202F]/g, "")
    .split(groupSeparator === "." || groupSeparator === "," ? groupSeparator : "\u0000")
    .join("");
  const normalised = decimalSeparator === "," ? withoutGroups.replace(",", ".") : withoutGroups;

  if (!/^\d+(\.\d+)?$/.test(normalised) || Number(normalised) <= 0) {
    return null;
  }

  return normalised.replace(/^0+(?=\d)/, "");
}

function useTypographicMinus(text: string): string {
  return text.replace("-", MINUS_SIGN);
}
