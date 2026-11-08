import { Search } from "lucide-react";
import { useDeferredValue, useId, useState, type KeyboardEvent } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router";

import { useAssets } from "../api/queries";

const MAX_SUGGESTIONS = 8;

/**
 * Finds any tracked asset by symbol or name and opens it. A combobox: arrow keys move through the
 * results, Enter opens the highlighted one, Escape closes the list.
 */
export function SearchBox() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const listId = useId();
  const [query, setQuery] = useState("");
  const [highlighted, setHighlighted] = useState(0);
  const [isOpen, setIsOpen] = useState(false);
  const deferredQuery = useDeferredValue(query.trim());
  const results = (useAssets(undefined, deferredQuery || undefined).data ?? []).slice(
    0,
    MAX_SUGGESTIONS,
  );
  const showList = isOpen && deferredQuery.length > 0;

  const open = (key: string) => {
    setQuery("");
    setIsOpen(false);
    void navigate(`/assets/${encodeURIComponent(key)}`);
  };

  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === "ArrowDown") {
      event.preventDefault();
      setHighlighted((index) => Math.min(index + 1, results.length - 1));
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      setHighlighted((index) => Math.max(index - 1, 0));
    } else if (event.key === "Enter" && results[highlighted]) {
      event.preventDefault();
      open(results[highlighted].key);
    } else if (event.key === "Escape") {
      setIsOpen(false);
    }
  };

  return (
    <div className="relative min-w-0 flex-1 md:max-w-md">
      <label htmlFor={`${listId}-input`} className="sr-only">
        {t("search.label")}
      </label>
      <Search
        aria-hidden
        className="pointer-events-none absolute start-3 top-1/2 size-4 -translate-y-1/2 text-muted"
        strokeWidth={1.5}
      />
      <input
        id={`${listId}-input`}
        type="search"
        role="combobox"
        aria-expanded={showList}
        aria-controls={listId}
        aria-autocomplete="list"
        aria-activedescendant={
          showList && results[highlighted] ? `${listId}-${results[highlighted].key}` : undefined
        }
        autoComplete="off"
        placeholder={t("search.placeholder")}
        value={query}
        onChange={(event) => {
          setQuery(event.target.value);
          setHighlighted(0);
          setIsOpen(true);
        }}
        onFocus={() => {
          setIsOpen(true);
        }}
        onBlur={() => {
          setIsOpen(false);
        }}
        onKeyDown={onKeyDown}
        className="w-full rounded-control border border-divider bg-background py-2 ps-9 pe-3 text-small placeholder:text-muted"
      />

      {showList && (
        <ul
          id={listId}
          role="listbox"
          className="absolute inset-x-0 top-full z-40 mt-1 overflow-hidden rounded-panel border border-divider bg-surface shadow-lg"
        >
          {results.length === 0 && (
            <li className="px-3 py-2 text-small text-muted">
              {t("search.noResults", { query: deferredQuery })}
            </li>
          )}
          {results.map((asset, index) => (
            <li
              key={asset.key}
              id={`${listId}-${asset.key}`}
              role="option"
              aria-selected={index === highlighted}
              onMouseDown={(event) => {
                event.preventDefault();
                open(asset.key);
              }}
              className={`flex cursor-pointer items-baseline justify-between gap-3 px-3 py-2 text-small ${index === highlighted ? "bg-background" : ""}`}
            >
              <span className="truncate">
                <span className="font-semibold">{asset.symbol}</span>{" "}
                <span className="text-muted">{asset.name}</span>
              </span>
              <span className="shrink-0 text-caption text-muted">
                {t(`assetClass.${asset.class}`)}
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
