import { formatPrice, parseAmount } from "@marketlens/shared/format";
import { ArrowUpDown } from "lucide-react";
import { useId, useState } from "react";
import { useTranslation } from "react-i18next";

import { ApiProblemError, type Asset } from "../api/client";
import { useLiveQuotes, useQuote } from "../api/liveQuotes";
import { useAllAssets, useConversion } from "../api/queries";
import { Freshness } from "../components/QuoteParts";
import { useSettings } from "../settings/Settings";

const exampleAmount = { en: "0.25", fr: "0,25", de: "0,25" } as const;

/**
 * Converts an amount between any two tracked assets. The server computes the rate (D36); this panel
 * multiplies the typed amount by the server's live rate only to show the result as prices move.
 */
export function ConverterPanel({
  initialFrom = "crypto:BTC",
  initialTo,
}: {
  initialFrom?: string;
  initialTo?: string;
}) {
  const { t } = useTranslation();
  const { language, displayCurrency } = useSettings();
  const [from, setFrom] = useState(initialFrom);
  const [to, setTo] = useState(
    initialTo ?? (displayCurrency === initialFrom ? "fiat:USD" : displayCurrency),
  );
  const [amountText, setAmountText] = useState("1");
  const amountId = useId();

  const amount = parseAmount(amountText, language);
  const conversion = useConversion(from, to, amount);
  useLiveQuotes(from === to ? [] : [from], to);
  const liveRate = useQuote(from, to);
  const assets = useAllAssets().data ?? [];
  const fromAsset = assets.find((asset) => asset.key === from);

  const rate = liveRate?.price ?? conversion.data?.rate;
  const converted =
    rate !== undefined && amount !== null ? (Number(amount) * Number(rate)).toString() : null;
  const symbol = (key: string) => key.split(":")[1] ?? key;
  const problem = conversion.error instanceof ApiProblemError ? conversion.error : null;

  return (
    <section aria-labelledby={`${amountId}-title`} className="rounded-panel bg-surface p-5">
      <h2 id={`${amountId}-title`} className="text-title font-semibold">
        {t("converter.title")}
      </h2>

      <div className="mt-4 grid gap-3">
        <label htmlFor={amountId} className="block">
          <span className="mb-1 block text-small text-muted">{t("converter.amount")}</span>
          <input
            id={amountId}
            inputMode="decimal"
            autoComplete="off"
            value={amountText}
            aria-invalid={amount === null}
            aria-describedby={amount === null ? `${amountId}-error` : undefined}
            onChange={(event) => {
              setAmountText(event.target.value);
            }}
            className="tabular w-full rounded-control border border-divider bg-background px-3 py-2 text-title"
          />
        </label>
        {amount === null && (
          <p id={`${amountId}-error`} className="text-small text-down">
            {t("converter.invalidAmount", { example: exampleAmount[language] })}
          </p>
        )}

        <AssetSelect label={t("converter.from")} value={from} assets={assets} onChange={setFrom} />
        <button
          type="button"
          onClick={() => {
            setFrom(to);
            setTo(from);
          }}
          className="mx-auto flex items-center gap-2 rounded-control border border-divider px-3 py-1.5 text-small hover:bg-background"
        >
          <ArrowUpDown aria-hidden className="size-4" strokeWidth={1.5} />
          {t("converter.swap")}
        </button>
        <AssetSelect label={t("converter.to")} value={to} assets={assets} onChange={setTo} />
      </div>

      <div aria-live="polite" className="mt-5 border-t border-divider pt-4">
        {from === to && <p className="text-small text-muted">{t("converter.sameAsset")}</p>}
        {problem?.isRateUnavailable && (
          <p className="text-small text-down">
            {t("converter.unavailable", { asset: problem.asset ?? "" })}
          </p>
        )}
        {from !== to && converted !== null && amount !== null && rate !== undefined && (
          <>
            <p className="text-small text-muted">
              {t("converter.result", { amount: formatPrice(amount, language), from: symbol(from) })}
            </p>
            <p className="tabular mt-1 text-display font-semibold">
              {formatPrice(converted, language)}{" "}
              <span className="text-title font-normal text-muted">{symbol(to)}</span>
            </p>
            <p className="mt-2 text-small text-muted">
              {t("converter.rate", {
                from: symbol(from),
                rate: formatPrice(rate, language),
                to: symbol(to),
              })}
            </p>
            {liveRate && fromAsset && (
              <p className="mt-1 text-caption">
                <Freshness quote={liveRate} assetClass={fromAsset.class} />
              </p>
            )}
          </>
        )}
      </div>
    </section>
  );
}

const classOrder = ["fiat", "crypto", "stock", "etf"] as const;

function AssetSelect({
  label,
  value,
  assets,
  onChange,
}: {
  label: string;
  value: string;
  assets: readonly Asset[];
  onChange: (key: string) => void;
}) {
  const { t } = useTranslation();

  return (
    <label className="block">
      <span className="mb-1 block text-small text-muted">{label}</span>
      <select
        value={value}
        onChange={(event) => {
          onChange(event.target.value);
        }}
        className="w-full rounded-control border border-divider bg-surface px-3 py-2"
      >
        {assets.length === 0 && <option value={value}>{value}</option>}
        {classOrder.map((assetClass) => (
          <optgroup key={assetClass} label={t(`assetClass.${assetClass}`)}>
            {assets
              .filter((asset) => asset.class === assetClass)
              .map((asset) => (
                <option key={asset.key} value={asset.key}>
                  {asset.symbol} – {asset.name}
                </option>
              ))}
          </optgroup>
        ))}
      </select>
    </label>
  );
}
