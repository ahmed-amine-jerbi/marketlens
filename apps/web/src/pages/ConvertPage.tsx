import { useTranslation } from "react-i18next";

import { PageTitle } from "../app/Layout";
import { ConverterPanel } from "../conversions/ConverterPanel";

export function ConvertPage() {
  const { t } = useTranslation();

  return (
    <div className="max-w-md">
      <PageTitle title={t("converter.title")} />
      <ConverterPanel headingLevel="h1" />
    </div>
  );
}
