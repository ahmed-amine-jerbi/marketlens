import { useTranslation } from "react-i18next";
import { Link, NavLink, Outlet } from "react-router";

import { SearchBox } from "./SearchBox";
import { SettingsMenu } from "./SettingsMenu";

const navigation = [
  { to: "/", label: "nav.home", end: true },
  { to: "/markets/currencies", label: "nav.currencies", end: false },
  { to: "/markets/crypto", label: "nav.crypto", end: false },
  { to: "/markets/stocks", label: "nav.stocks", end: false },
  { to: "/convert", label: "nav.convert", end: false },
] as const;

/** The frame around every page: header with search and settings, navigation, footer. */
export function Layout() {
  const { t } = useTranslation();

  return (
    <div className="min-h-dvh">
      <a
        href="#content"
        className="sr-only focus:not-sr-only focus:fixed focus:start-4 focus:top-4 focus:z-50 focus:rounded-control focus:bg-surface focus:px-4 focus:py-2"
      >
        {t("app.skipToContent")}
      </a>

      <header className="sticky top-0 z-30 border-b border-divider bg-surface">
        <div className="mx-auto flex max-w-7xl items-center gap-4 px-4 py-3 md:px-6">
          <Link to="/" className="flex shrink-0 items-center gap-2 text-title font-semibold">
            <img src="/favicon.svg" alt="" className="size-7" />
            <span className="hidden sm:inline">{t("app.name")}</span>
          </Link>
          <SearchBox />
          <SettingsMenu />
        </div>
        <nav aria-label={t("nav.label")} className="md:hidden">
          <ul className="flex gap-1 overflow-x-auto px-3 pb-2">
            {navigation.map((item) => (
              <li key={item.to} className="shrink-0">
                <NavLink to={item.to} end={item.end} className={navLinkClass}>
                  {t(item.label)}
                </NavLink>
              </li>
            ))}
          </ul>
        </nav>
      </header>

      <div className="mx-auto flex max-w-7xl gap-8 px-4 md:px-6">
        <nav aria-label={t("nav.label")} className="hidden w-44 shrink-0 pt-6 md:block">
          <ul className="sticky top-24 space-y-1">
            {navigation.map((item) => (
              <li key={item.to}>
                <NavLink to={item.to} end={item.end} className={navLinkClass}>
                  {t(item.label)}
                </NavLink>
              </li>
            ))}
          </ul>
        </nav>
        <main id="content" className="min-w-0 flex-1 py-6">
          <Outlet />
        </main>
      </div>

      <footer className="border-t border-divider">
        <div className="mx-auto flex max-w-7xl flex-col gap-2 px-4 py-6 text-small text-muted sm:flex-row sm:justify-between md:px-6">
          <p>{t("app.notAdvice")}</p>
          <Link to="/data-sources" className="underline underline-offset-4 hover:text-ink">
            {t("nav.dataSources")}
          </Link>
        </div>
      </footer>
    </div>
  );
}

function navLinkClass({ isActive }: { isActive: boolean }): string {
  const base = "block rounded-control px-3 py-2 text-small";
  return isActive
    ? `${base} bg-background font-semibold text-ink`
    : `${base} text-muted hover:text-ink`;
}

/** Shown when a page throws: the shell stays usable and the visitor can reload or go home. */
export function RouteError() {
  const { t } = useTranslation();

  return (
    <main className="mx-auto max-w-xl px-4 py-16">
      <h1 className="text-heading font-semibold">{t("errors.unexpected")}</h1>
      <Link to="/" className="mt-4 inline-block text-accent underline underline-offset-4">
        {t("errors.backHome")}
      </Link>
    </main>
  );
}
