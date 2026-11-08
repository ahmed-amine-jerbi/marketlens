import { useTranslation } from "react-i18next";
import { Link } from "react-router";

const providers = [
  { name: "Frankfurter", href: "https://frankfurter.dev", text: "dataSources.fiat" },
  { name: "Binance", href: "https://www.binance.com", text: "dataSources.crypto" },
  { name: "CoinGecko", href: "https://www.coingecko.com", text: "dataSources.cryptoNames" },
  { name: "Finnhub · Twelve Data", href: "https://finnhub.io", text: "dataSources.stocks" },
] as const;

/** Where every price comes from, how current it is, and the not-financial-advice notice (R13, R14). */
export function DataSourcesPage() {
  const { t } = useTranslation();

  return (
    <article className="max-w-2xl">
      <h1 className="text-heading font-semibold">{t("dataSources.title")}</h1>
      <p className="mt-3 text-body text-muted">{t("dataSources.intro")}</p>

      <dl className="mt-8 divide-y divide-divider rounded-panel bg-surface px-5">
        {providers.map((provider) => (
          <div key={provider.name} className="grid gap-1 py-4 sm:grid-cols-[10rem_1fr] sm:gap-6">
            <dt className="font-semibold">
              <a
                href={provider.href}
                target="_blank"
                rel="noreferrer"
                className="underline underline-offset-4 hover:text-accent"
              >
                {provider.name}
              </a>
            </dt>
            <dd className="text-small text-muted">{t(provider.text)}</dd>
          </div>
        ))}
      </dl>

      <p className="mt-6 text-small text-muted">{t("dataSources.conversions")}</p>
      <p className="mt-2 text-small text-muted">
        {t("dataSources.forecasts")}{" "}
        <Link to="/how-forecasts-work" className="text-accent underline underline-offset-4">
          {t("forecast.howItWorks")}
        </Link>
      </p>

      <section aria-labelledby="disclaimer-title" className="mt-10 border-t border-divider pt-6">
        <h2 id="disclaimer-title" className="text-title font-semibold">
          {t("dataSources.disclaimerTitle")}
        </h2>
        <p className="mt-2 text-body">{t("dataSources.disclaimer")}</p>
      </section>
    </article>
  );
}
