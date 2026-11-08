import { isLanguage, type Language } from "./format";

export interface LanguageSignals {
  /** The language the user picked, if any; it always wins. */
  saved: string | null;
  /** The device or browser languages, most preferred first (navigator.languages). */
  deviceLanguages: readonly string[];
  /** The API's locale suggestion, used only when the device offers no supported language. */
  suggested: string | null;
}

/**
 * Picks the app language (D22): the saved choice, then the first supported device language, then
 * the server's suggestion, then English.
 */
export function detectLanguage({ saved, deviceLanguages, suggested }: LanguageSignals): Language {
  if (isLanguage(saved)) {
    return saved;
  }

  const fromDevice = deviceLanguages
    .map((tag) => tag.split("-")[0]?.toLowerCase())
    .find((primary) => isLanguage(primary));
  if (isLanguage(fromDevice)) {
    return fromDevice;
  }

  return isLanguage(suggested) ? suggested : "en";
}
