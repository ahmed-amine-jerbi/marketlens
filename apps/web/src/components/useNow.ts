import { useSyncExternalStore } from "react";

/** One shared clock, ticking every second, for "updated 2 sec. ago" labels. */
const TICK_MS = 1000;
const listeners = new Set<() => void>();
let now = new Date();
let timer: number | undefined;

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  timer ??= window.setInterval(() => {
    now = new Date();
    listeners.forEach((notify) => {
      notify();
    });
  }, TICK_MS);

  return () => {
    listeners.delete(listener);
    if (listeners.size === 0 && timer !== undefined) {
      window.clearInterval(timer);
      timer = undefined;
    }
  };
}

export function useNow(): Date {
  return useSyncExternalStore(subscribe, () => now);
}
