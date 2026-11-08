import de from "@marketlens/shared/i18n/de.json";
import en from "@marketlens/shared/i18n/en.json";
import fr from "@marketlens/shared/i18n/fr.json";
import i18n from "i18next";
import { initReactI18next } from "react-i18next";

// Type-checked keys: t("home.title") compiles, a misspelt key doesn't.
declare module "i18next" {
  interface CustomTypeOptions {
    defaultNS: "translation";
    resources: { translation: typeof en };
  }
}

void i18n.use(initReactI18next).init({
  resources: { en: { translation: en }, fr: { translation: fr }, de: { translation: de } },
  lng: "en",
  fallbackLng: "en",
  // React escapes rendered text already.
  interpolation: { escapeValue: false },
});

export default i18n;
