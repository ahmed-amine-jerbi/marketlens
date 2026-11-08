import { useSyncExternalStore } from "react";

/**
 * Small values kept on this device (settings, the signed-out watchlist), shared by every component
 * and every open tab. Storage can be unavailable (private mode, blocked site data); the app then
 * keeps working from memory for the session.
 */
type Listener = () => void;

const memoryFallback = new Map<string, string>();
const listeners = new Map<string, Set<Listener>>();

function read(key: string): string | null {
  try {
    return window.localStorage.getItem(key);
  } catch {
    // Storage blocked: fall back to this session's memory.
    return memoryFallback.get(key) ?? null;
  }
}

function notify(key: string): void {
  listeners.get(key)?.forEach((listener) => {
    listener();
  });
}

export function writeDeviceValue(key: string, value: string | null): void {
  try {
    if (value === null) {
      window.localStorage.removeItem(key);
    } else {
      window.localStorage.setItem(key, value);
    }
  } catch {
    // Storage blocked: keep the value for this session only.
    if (value === null) {
      memoryFallback.delete(key);
    } else {
      memoryFallback.set(key, value);
    }
  }

  notify(key);
}

function subscribe(key: string, listener: Listener): () => void {
  const forKey = listeners.get(key) ?? new Set<Listener>();
  forKey.add(listener);
  listeners.set(key, forKey);

  const onOtherTab = (event: StorageEvent) => {
    if (event.key === key) {
      listener();
    }
  };
  window.addEventListener("storage", onOtherTab);

  return () => {
    forKey.delete(listener);
    window.removeEventListener("storage", onOtherTab);
  };
}

/** The raw string stored under a key on this device, updating when any tab changes it. */
export function useDeviceValue(key: string): string | null {
  return useSyncExternalStore(
    (listener) => subscribe(key, listener),
    () => read(key),
    () => null,
  );
}
