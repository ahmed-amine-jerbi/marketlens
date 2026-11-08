import { detectLanguage } from "@marketlens/shared/detectLanguage";
import type { Language } from "@marketlens/shared/format";
import { createContext, use, useEffect, useMemo, type ReactNode } from "react";
import { useTranslation } from "react-i18next";

import { useLocaleSuggestion } from "../api/queries";
import { useDeviceValue, writeDeviceValue } from "./deviceStorage";

export const themeChoices = ["system", "light", "dark"] as const;
export type ThemeChoice = (typeof themeChoices)[number];

const LANGUAGE_KEY = "marketlens.language";
const CURRENCY_KEY = "marketlens.displayCurrency";
const THEME_KEY = "marketlens.theme";
const DEFAULT_CURRENCY = "fiat:USD";

export interface Settings {
  language: Language;
  /** The asset every price is shown in (D36), for example "fiat:EUR". */
  displayCurrency: string;
  theme: ThemeChoice;
  setLanguage: (language: Language) => void;
  setDisplayCurrency: (assetKey: string) => void;
  setTheme: (theme: ThemeChoice) => void;
}

const SettingsContext = createContext<Settings | null>(null);

const isThemeChoice = (value: string | null): value is ThemeChoice =>
  themeChoices.some((choice) => choice === value);

/**
 * Owns the visitor's language, display currency and theme: saved choices win; otherwise the device
 * languages and the API's locale suggestion decide (D22). Applies them to the document.
 */
export function SettingsProvider({ children }: { children: ReactNode }) {
  const savedLanguage = useDeviceValue(LANGUAGE_KEY);
  const savedCurrency = useDeviceValue(CURRENCY_KEY);
  const savedTheme = useDeviceValue(THEME_KEY);
  const suggestion = useLocaleSuggestion(savedLanguage === null || savedCurrency === null).data;
  const { i18n } = useTranslation();

  const language = detectLanguage({
    saved: savedLanguage,
    deviceLanguages: navigator.languages,
    suggested: suggestion?.language ?? null,
  });
  const displayCurrency = savedCurrency ?? suggestion?.displayCurrency ?? DEFAULT_CURRENCY;
  const theme = isThemeChoice(savedTheme) ? savedTheme : "system";

  useEffect(() => {
    document.documentElement.lang = language;
    void i18n.changeLanguage(language);
  }, [language, i18n]);

  useEffect(() => {
    if (theme === "system") {
      delete document.documentElement.dataset.theme;
    } else {
      document.documentElement.dataset.theme = theme;
    }
  }, [theme]);

  const settings = useMemo<Settings>(
    () => ({
      language,
      displayCurrency,
      theme,
      setLanguage: (next) => {
        writeDeviceValue(LANGUAGE_KEY, next);
      },
      setDisplayCurrency: (next) => {
        writeDeviceValue(CURRENCY_KEY, next);
      },
      setTheme: (next) => {
        writeDeviceValue(THEME_KEY, next);
      },
    }),
    [language, displayCurrency, theme],
  );

  return <SettingsContext value={settings}>{children}</SettingsContext>;
}

export function useSettings(): Settings {
  const settings = use(SettingsContext);
  if (!settings) {
    throw new Error("useSettings must be used inside <SettingsProvider>.");
  }

  return settings;
}
