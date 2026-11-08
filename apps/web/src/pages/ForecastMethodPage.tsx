import { useTranslation } from "react-i18next";

const sections = [
  { title: "forecastMethod.rangeTitle", text: "forecastMethod.range" },
  { title: "forecastMethod.madeTitle", text: "forecastMethod.made" },
  { title: "forecastMethod.chosenTitle", text: "forecastMethod.chosen" },
  { title: "forecastMethod.recordTitle", text: "forecastMethod.record" },
  { title: "forecastMethod.limitsTitle", text: "forecastMethod.limits" },
  { title: "forecastMethod.adviceTitle", text: "forecastMethod.advice" },
] as const;

/** How the forecast range is made, chosen and measured, and what it can't do (R14). */
export function ForecastMethodPage() {
  const { t } = useTranslation();

  return (
    <article className="max-w-2xl">
      <h1 className="text-heading font-semibold">{t("forecastMethod.title")}</h1>
      <p className="mt-3 text-body text-muted">{t("forecastMethod.intro")}</p>

      {sections.map((section) => (
        <section key={section.title} className="mt-8">
          <h2 className="text-title font-semibold">{t(section.title)}</h2>
          <p className="mt-2 text-body">{t(section.text)}</p>
        </section>
      ))}
    </article>
  );
}
