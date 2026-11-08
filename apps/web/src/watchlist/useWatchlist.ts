import { useCallback, useMemo } from "react";

import { useDeviceValue, writeDeviceValue } from "../settings/deviceStorage";

const STORAGE_KEY = "marketlens.watchlist";

/** The API accepts at most 200 watchlist entries (plan, data model). */
export const MAX_WATCHLIST_SIZE = 200;

const ASSET_KEY_PATTERN = /^[a-z]+:[A-Z0-9][A-Z0-9.-]{0,14}$/;

/** Reads a stored watchlist, dropping anything that isn't an asset key. */
export function parseWatchlist(stored: string | null): string[] {
  if (stored === null) {
    return [];
  }

  try {
    const parsed: unknown = JSON.parse(stored);
    return Array.isArray(parsed)
      ? [
          ...new Set(
            parsed.filter(
              (key): key is string => typeof key === "string" && ASSET_KEY_PATTERN.test(key),
            ),
          ),
        ]
      : [];
  } catch {
    // A corrupted value starts an empty watchlist rather than breaking the page.
    return [];
  }
}

/**
 * The signed-out watchlist (D14): an ordered list of asset keys kept on this device. Syncing it to
 * an account arrives with sign-in (M5).
 */
export function useWatchlist() {
  const stored = useDeviceValue(STORAGE_KEY);
  const keys = useMemo(() => parseWatchlist(stored), [stored]);

  const save = useCallback((next: readonly string[]) => {
    writeDeviceValue(STORAGE_KEY, JSON.stringify(next.slice(0, MAX_WATCHLIST_SIZE)));
  }, []);

  const add = useCallback(
    (key: string) => {
      if (!keys.includes(key)) {
        save([...keys, key]);
      }
    },
    [keys, save],
  );

  const remove = useCallback(
    (key: string) => {
      save(keys.filter((watched) => watched !== key));
    },
    [keys, save],
  );

  const has = useCallback((key: string) => keys.includes(key), [keys]);

  return { keys, add, remove, has };
}
