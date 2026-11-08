import { act, renderHook } from "@testing-library/react";
import { beforeEach, describe, expect, it } from "vitest";

import { MAX_WATCHLIST_SIZE, parseWatchlist, useWatchlist } from "./useWatchlist";

describe("parseWatchlist", () => {
  it("keeps valid asset keys in order and drops duplicates", () => {
    expect(parseWatchlist('["crypto:BTC","fiat:EUR","crypto:BTC"]')).toEqual([
      "crypto:BTC",
      "fiat:EUR",
    ]);
  });

  it("drops anything that isn't an asset key", () => {
    expect(parseWatchlist('["crypto:BTC", 42, "<script>", "stock:aapl", null]')).toEqual([
      "crypto:BTC",
    ]);
  });

  it.each([null, "", "not json", '{"crypto:BTC":true}'])("starts empty from %j", (stored) => {
    expect(parseWatchlist(stored)).toEqual([]);
  });
});

describe("useWatchlist", () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it("adds, keeps the order and removes", () => {
    const { result } = renderHook(() => useWatchlist());

    act(() => {
      result.current.add("crypto:BTC");
    });
    act(() => {
      result.current.add("fiat:EUR");
    });
    act(() => {
      result.current.add("crypto:BTC");
    });
    expect(result.current.keys).toEqual(["crypto:BTC", "fiat:EUR"]);
    expect(result.current.has("fiat:EUR")).toBe(true);

    act(() => {
      result.current.remove("crypto:BTC");
    });
    expect(result.current.keys).toEqual(["fiat:EUR"]);
  });

  it("survives a reload because it is saved on the device", () => {
    const first = renderHook(() => useWatchlist());
    act(() => {
      first.result.current.add("stock:AAPL");
    });
    first.unmount();

    const second = renderHook(() => useWatchlist());

    expect(second.result.current.keys).toEqual(["stock:AAPL"]);
  });

  it("stops at the API's limit", () => {
    const tooMany = Array.from(
      { length: MAX_WATCHLIST_SIZE + 5 },
      (_, index) => `stock:S${index.toString()}`,
    );
    window.localStorage.setItem("marketlens.watchlist", JSON.stringify(tooMany));
    const { result } = renderHook(() => useWatchlist());

    act(() => {
      result.current.add("crypto:BTC");
    });

    expect(result.current.keys).toHaveLength(MAX_WATCHLIST_SIZE);
  });
});
