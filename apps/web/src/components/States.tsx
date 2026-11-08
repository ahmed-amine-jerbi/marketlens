import { useTranslation } from "react-i18next";

/** Placeholder rows shaped like the table that will replace them, so nothing jumps on arrival. */
export function TableSkeleton({ rows = 6 }: { rows?: number }) {
  return (
    <div aria-hidden className="divide-y divide-divider">
      {Array.from({ length: rows }, (_, row) => (
        <div key={row} className="flex items-center gap-3 py-3">
          <span className="size-6 animate-pulse rounded-full bg-background" />
          <span className="h-4 w-40 animate-pulse rounded-control bg-background" />
          <span className="ms-auto h-4 w-20 animate-pulse rounded-control bg-background" />
        </div>
      ))}
    </div>
  );
}

/** What went wrong, in plain words, and a way to try again. */
export function LoadError({ onRetry }: { onRetry: () => void }) {
  const { t } = useTranslation();

  return (
    <div role="alert" className="rounded-panel border border-divider bg-surface p-4 text-small">
      <p>{t("errors.loadFailed")}</p>
      <button
        type="button"
        onClick={onRetry}
        className="mt-3 rounded-control border border-divider px-3 py-2 font-semibold hover:bg-background"
      >
        {t("errors.retry")}
      </button>
    </div>
  );
}
