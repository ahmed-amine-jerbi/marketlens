import { useTranslation } from "react-i18next";
import { Link } from "react-router";

export function NotFoundPage() {
  const { t } = useTranslation();

  return (
    <div className="max-w-xl">
      <h1 className="text-heading font-semibold">{t("errors.pageNotFound")}</h1>
      <Link to="/" className="mt-4 inline-block text-accent underline underline-offset-4">
        {t("errors.backHome")}
      </Link>
    </div>
  );
}
