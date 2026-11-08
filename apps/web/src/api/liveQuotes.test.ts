import { act, renderHook } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { Quote } from "./client";
import { storeQuotes, useQuote } from "./liveQuotes";

const quote = (key: string, price: string, asOfUtc: string, displayIn = "fiat:EUR"): Quote => ({
  key,
  price,
  in: displayIn,
  changePct: "0.5",
  changeBasis: "rolling-24h",
  asOfUtc,
  source: "binance",
  freshness: "live",
});

describe("live quote store", () => {
  it("gives each component the latest quote of its asset in its currency", () => {
    const { result } = renderHook(() => useQuote("crypto:SOL", "fiat:EUR"));
    expect(result.current).toBeUndefined();

    act(() => {
      storeQuotes([quote("crypto:SOL", "140.10", "2026-11-08T16:00:00Z")]);
    });
    expect(result.current?.price).toBe("140.10");

    act(() => {
      storeQuotes([quote("crypto:SOL", "140.55", "2026-11-08T16:00:01Z")]);
    });
    expect(result.current?.price).toBe("140.55");
  });

  it("keeps the same asset in another currency separate", () => {
    const { result } = renderHook(() => useQuote("crypto:ADA", "fiat:TND"));

    act(() => {
      storeQuotes([quote("crypto:ADA", "0.45", "2026-11-08T16:00:00Z", "fiat:EUR")]);
    });

    expect(result.current).toBeUndefined();
  });

  it("ignores an older copy of a price it already has", () => {
    const { result } = renderHook(() => useQuote("crypto:DOT", "fiat:EUR"));

    act(() => {
      storeQuotes([quote("crypto:DOT", "4.12", "2026-11-08T16:00:05Z")]);
      storeQuotes([quote("crypto:DOT", "4.12", "2026-11-08T16:00:01Z")]);
    });

    expect(result.current?.asOfUtc).toBe("2026-11-08T16:00:05Z");
  });
});
