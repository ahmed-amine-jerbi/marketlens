import { supportedLanguages, type Language } from "@marketlens/shared/format";
import { Settings2 } from "lucide-react";
import { useId } from "react";
import { useTranslation } from "react-i18next";

import { useAssets } from "../api/queries";
import { themeChoices, useSettings, type ThemeChoice } from "../settings/Settings";

const languageNames: Record<Language, string> = { en: "English", fr: "Français", de: "Deutsch" };
const themeLabels = {
  system: "settings.themeSystem",
  light: "settings.themeLight",
  dark: "settings.themeDark",
} as const;

/** Language, display currency and theme, in a native popover so focus and Escape just work. */
export function SettingsMenu() {
  const { t } = useTranslation();
  const settings = useSettings();
  const currencies = useAssets("fiat").data ?? [];
  const popoverId = useId();

  return (
    <div className="ms-auto">
      <button
        type="button"
        popoverTarget={popoverId}
        className="flex items-center gap-2 rounded-control border border-divider px-3 py-2 text-small hover:bg-background"
      >
        <Settings2 aria-hidden className="size-4" strokeWidth={1.5} />
        <span>{settings.language.toUpperCase()}</span>
        <span className="text-muted">{settings.displayCurrency.replace("fiat:", "")}</span>
        <span className="sr-only">{t("settings.open")}</span>
      </button>

      <div
        id={popoverId}
        popover="auto"
        className="inset-auto end-4 top-16 m-0 w-72 rounded-panel border border-divider bg-surface p-4 text-ink shadow-lg"
      >
        <div className="space-y-4">
          <label className="block">
            <span className="mb-1 block text-small text-muted">{t("settings.language")}</span>
            <select
              value={settings.language}
              onChange={(event) => {
                settings.setLanguage(event.target.value as Language);
              }}
              className="w-full rounded-control border border-divider bg-surface px-3 py-2"
            >
              {supportedLanguages.map((language) => (
                <option key={language} value={language} lang={language}>
                  {languageNames[language]}
                </option>
              ))}
            </select>
          </label>

          <label className="block">
            <span className="mb-1 block text-small text-muted">
              {t("settings.displayCurrency")}
            </span>
            <select
              value={settings.displayCurrency}
              onChange={(event) => {
                settings.setDisplayCurrency(event.target.value);
              }}
              className="w-full rounded-control border border-divider bg-surface px-3 py-2"
            >
              {currencies.length === 0 && (
                <option value={settings.displayCurrency}>
                  {settings.displayCurrency.replace("fiat:", "")}
                </option>
              )}
              {currencies.map((currency) => (
                <option key={currency.key} value={currency.key}>
                  {currency.symbol} – {currency.name}
                </option>
              ))}
            </select>
          </label>

          <fieldset>
            <legend className="mb-1 text-small text-muted">{t("settings.theme")}</legend>
            <div className="flex gap-2">
              {themeChoices.map((choice: ThemeChoice) => (
                <label key={choice} className="flex-1">
                  <input
                    type="radio"
                    name="theme"
                    value={choice}
                    checked={settings.theme === choice}
                    onChange={() => {
                      settings.setTheme(choice);
                    }}
                    className="peer sr-only"
                  />
                  <span className="block cursor-pointer rounded-control border border-divider px-2 py-2 text-center text-small peer-checked:border-accent peer-checked:font-semibold peer-focus-visible:outline-2 peer-focus-visible:outline-accent">
                    {t(themeLabels[choice])}
                  </span>
                </label>
              ))}
            </div>
          </fieldset>
        </div>
      </div>
    </div>
  );
}
