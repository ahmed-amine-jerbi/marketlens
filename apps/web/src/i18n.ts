import type { Language } from "@marketlens/shared/format";
import en from "@marketlens/shared/i18n/en.json";
import i18n from "i18next";
import { initReactI18next } from "react-i18next";

// Type-checked keys: t("home.title") compiles, a misspelt key doesn't.
declare module "i18next" {
  interface CustomTypeOptions {
    defaultNS: "translation";
    resources: { translation: typeof en };
  }
}

// English ships with the app (it is also the fallback); other languages load when chosen, so the
// first download carries one set of messages instead of three.
const otherLanguages: Record<Exclude<Language, "en">, () => Promise<{ default: typeof en }>> = {
  fr: () => import("@marketlens/shared/i18n/fr.json"),
  de: () => import("@marketlens/shared/i18n/de.json"),
};

void i18n.use(initReactI18next).init({
  resources: { en: { translation: en } },
  lng: "en",
  fallbackLng: "en",
  // React escapes rendered text already.
  interpolation: { escapeValue: false },
});

let requestedLanguage: Language = "en";

/**
 * Loads a language's messages if needed, then switches the app to it, unless the visitor chose
 * another language while these messages were loading: the latest choice wins.
 */
export async function switchLanguage(language: Language): Promise<void> {
  requestedLanguage = language;
  if (language !== "en" && !i18n.hasResourceBundle(language, "translation")) {
    const messages = await otherLanguages[language]();
    i18n.addResourceBundle(language, "translation", messages.default);
  }

  if (requestedLanguage === language) {
    await i18n.changeLanguage(language);
  }
}

export default i18n;
