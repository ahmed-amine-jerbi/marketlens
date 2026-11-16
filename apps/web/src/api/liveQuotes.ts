import type * as SignalRModule from "@microsoft/signalr";
import type { HubConnection } from "@microsoft/signalr";
import { useEffect, useSyncExternalStore } from "react";

import type { Quote } from "./client";

/**
 * The latest quote of every asset on screen, per display currency. REST answers seed it and the
 * live hub (/hubs/quotes) keeps it current. Components read one entry each, so a price update
 * re-renders only the rows showing that price.
 */
type QuoteId = `${string}|${string}`;
type Listener = () => void;

const quotes = new Map<QuoteId, Quote>();
const listeners = new Map<QuoteId, Set<Listener>>();

const idOf = (key: string, displayIn: string): QuoteId => `${key}|${displayIn}`;

export function storeQuotes(received: readonly Quote[]): void {
  for (const quote of received) {
    const id = idOf(quote.key, quote.in);
    const current = quotes.get(id);
    if (current && current.asOfUtc > quote.asOfUtc && current.price === quote.price) {
      continue;
    }

    quotes.set(id, quote);
    listeners.get(id)?.forEach((notify) => {
      notify();
    });
  }
}

function subscribeTo(id: QuoteId, listener: Listener): () => void {
  const forId = listeners.get(id) ?? new Set<Listener>();
  forId.add(listener);
  listeners.set(id, forId);
  return () => {
    forId.delete(listener);
    if (forId.size === 0) {
      listeners.delete(id);
    }
  };
}

/** The current quote of one asset in one display currency, or undefined until it arrives. */
export function useQuote(key: string, displayIn: string): Quote | undefined {
  const id = idOf(key, displayIn);
  return useSyncExternalStore(
    (listener) => subscribeTo(id, listener),
    () => quotes.get(id),
  );
}

// One connection for the whole app; the hub allows a few per visitor. The SignalR client loads with
// the first live subscription, so it stays out of the first download.
type SignalR = typeof SignalRModule;
let signalR: SignalR | undefined;
let connection: HubConnection | undefined;
const watchers = new Map<string, number>();

// After automatic reconnection gives up, the connection is restarted on this schedule (the last
// delay repeats) for as long as something on screen is live.
const RESTART_DELAYS_MS = [5_000, 15_000, 30_000, 60_000] as const;
let restartAttempt = 0;

async function getConnection(): Promise<{ hub: HubConnection; signalR: SignalR }> {
  signalR ??= await import("@microsoft/signalr");
  if (connection) {
    return { hub: connection, signalR };
  }

  const hub = new signalR.HubConnectionBuilder()
    .withUrl("/hubs/quotes")
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Warning)
    .build();
  hub.on("QuoteUpdated", (quote: Quote) => {
    storeQuotes([quote]);
  });
  hub.onreconnected(() => {
    void resubscribeAll();
  });
  hub.onclose(() => {
    scheduleRestart();
  });
  connection = hub;
  return { hub, signalR };
}

function scheduleRestart(): void {
  if (watchers.size === 0) {
    return;
  }

  const delayMs = RESTART_DELAYS_MS[Math.min(restartAttempt, RESTART_DELAYS_MS.length - 1)];
  restartAttempt += 1;
  setTimeout(() => {
    resubscribeAll()
      .then(() => {
        restartAttempt = 0;
      })
      .catch((error: unknown) => {
        console.warn("Live quotes still unavailable", error);
        scheduleRestart();
      });
  }, delayMs);
}

async function subscribe(keys: readonly string[], displayIn: string): Promise<void> {
  const { hub, signalR: client } = await getConnection();
  if (hub.state === client.HubConnectionState.Disconnected) {
    await hub.start();
  }

  if (hub.state !== client.HubConnectionState.Connected) {
    return;
  }

  storeQuotes(await hub.invoke<Quote[]>("Subscribe", keys, displayIn));
}

async function resubscribeAll(): Promise<void> {
  const byCurrency = new Map<string, string[]>();
  for (const watched of watchers.keys()) {
    const [key = "", displayIn = ""] = watched.split("|");
    byCurrency.set(displayIn, [...(byCurrency.get(displayIn) ?? []), key]);
  }

  for (const [displayIn, keys] of byCurrency) {
    await subscribe(keys, displayIn);
  }
}

/**
 * Keeps the given assets live in a display currency while the calling component is mounted.
 * Counts watchers so two components showing the same asset share one subscription.
 */
export function useLiveQuotes(keys: readonly string[], displayIn: string): void {
  const keyList = keys.join(",");

  useEffect(() => {
    const watched = keyList === "" ? [] : keyList.split(",");
    const added = watched.filter((key) => {
      const id = idOf(key, displayIn);
      const count = watchers.get(id) ?? 0;
      watchers.set(id, count + 1);
      return count === 0;
    });

    if (added.length > 0) {
      subscribe(added, displayIn).catch((error: unknown) => {
        // Live updates are an enhancement: REST already shows the prices, so a failed subscribe
        // only means they stop moving until the connection comes back.
        console.warn("Live quotes unavailable", error);
      });
    }

    return () => {
      const removed = watched.filter((key) => {
        const id = idOf(key, displayIn);
        const count = (watchers.get(id) ?? 1) - 1;
        if (count === 0) {
          watchers.delete(id);
        } else {
          watchers.set(id, count);
        }

        return count === 0;
      });
      const stillWatchedElsewhere = new Set([...watchers.keys()].map((id) => id.split("|")[0]));
      const unwatched = removed.filter((key) => !stillWatchedElsewhere.has(key));
      const hub = connection;
      if (
        unwatched.length > 0 &&
        hub !== undefined &&
        hub.state === signalR?.HubConnectionState.Connected
      ) {
        hub.invoke("Unsubscribe", unwatched).catch((error: unknown) => {
          console.warn("Unsubscribe failed", error);
        });
      }
    };
  }, [keyList, displayIn]);
}
