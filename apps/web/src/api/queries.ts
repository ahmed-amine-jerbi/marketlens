import { keepPreviousData, useQueries, useQuery } from "@tanstack/react-query";

import { api, unwrap, type AssetClass } from "./client";
import { storeQuotes } from "./liveQuotes";

export type ChartRange = "1D" | "1W" | "1M" | "3M" | "1Y" | "5Y";
export const chartRanges: readonly ChartRange[] = ["1D", "1W", "1M", "3M", "1Y", "5Y"];

const ONE_HOUR = 60 * 60 * 1000;
const ONE_MINUTE = 60 * 1000;

/** The API serves up to 200 assets a page; every class fits in one page today. */
const PAGE_SIZE = 200;

/** The API answers at most 50 quotes per request. */
const QUOTES_PER_REQUEST = 50;

export function useAssets(assetClass?: AssetClass, search?: string) {
  return useQuery({
    queryKey: ["assets", assetClass ?? "all", search ?? ""],
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET("/api/v1/assets", {
          params: {
            query: {
              limit: PAGE_SIZE,
              ...(assetClass ? { class: assetClass } : {}),
              ...(search ? { search } : {}),
            },
          },
          signal,
        }),
      ).items,
    staleTime: ONE_HOUR,
    placeholderData: keepPreviousData,
  });
}

const assetClasses: readonly AssetClass[] = ["fiat", "crypto", "stock", "etf"];

/**
 * Every tracked asset. Loaded class by class: one page holds at most 200 assets, and all classes
 * together are more than that.
 */
export function useAllAssets() {
  return useQueries({
    queries: assetClasses.map((assetClass) => ({
      queryKey: ["assets", assetClass, ""],
      queryFn: async ({ signal }: { signal: AbortSignal }) =>
        unwrap(
          await api.GET("/api/v1/assets", {
            params: { query: { class: assetClass, limit: PAGE_SIZE } },
            signal,
          }),
        ).items,
      staleTime: ONE_HOUR,
    })),
    combine: (results) => ({
      data: results.every((result) => result.isSuccess)
        ? results.flatMap((result) => result.data)
        : undefined,
      isPending: results.some((result) => result.isPending),
      isError: results.some((result) => result.isError),
      refetch: () => Promise.all(results.map((result) => result.refetch())),
    }),
  });
}

export function useAsset(key: string, displayIn: string) {
  return useQuery({
    queryKey: ["asset", key, displayIn],
    queryFn: async ({ signal }) => {
      const detail = unwrap(
        await api.GET("/api/v1/assets/{key}", {
          params: { path: { key }, query: { in: displayIn } },
          signal,
        }),
      );
      if (detail.quote) {
        storeQuotes([detail.quote]);
      }

      return detail;
    },
    staleTime: ONE_MINUTE,
  });
}

/** Loads quotes for any number of assets (in batches of 50) into the live quote store. */
export function useQuotes(keys: readonly string[], displayIn: string) {
  return useQuery({
    queryKey: ["quotes", [...keys].sort().join(","), displayIn],
    enabled: keys.length > 0,
    queryFn: async ({ signal }) => {
      const batches: string[][] = [];
      for (let start = 0; start < keys.length; start += QUOTES_PER_REQUEST) {
        batches.push(keys.slice(start, start + QUOTES_PER_REQUEST));
      }

      const answers = await Promise.all(
        batches.map(async (batch) =>
          unwrap(
            await api.GET("/api/v1/quotes", {
              params: { query: { keys: batch.join(","), in: displayIn } },
              signal,
            }),
          ),
        ),
      );
      const quotes = answers.flat();
      storeQuotes(quotes);
      return quotes;
    },
    staleTime: ONE_MINUTE,
  });
}

export function useHistory(key: string, range: ChartRange, displayIn: string) {
  return useQuery({
    queryKey: ["history", key, range, displayIn],
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET("/api/v1/assets/{key}/history", {
          params: { path: { key }, query: { range, in: displayIn } },
          signal,
        }),
      ),
    staleTime: ONE_MINUTE,
    placeholderData: keepPreviousData,
  });
}

export function useConversion(from: string, to: string, amount: string | null) {
  return useQuery({
    queryKey: ["conversion", from, to, amount],
    enabled: amount !== null && from !== to,
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET("/api/v1/conversions", {
          params: { query: { from, to, amount: amount ?? "1" } },
          signal,
        }),
      ),
    staleTime: 10 * 1000,
    placeholderData: keepPreviousData,
  });
}

export function useStatus() {
  return useQuery({
    queryKey: ["status"],
    queryFn: async ({ signal }) => unwrap(await api.GET("/api/v1/status", { signal })),
    refetchInterval: ONE_MINUTE,
  });
}

export function useLocaleSuggestion(isNeeded: boolean) {
  return useQuery({
    queryKey: ["locale-suggestion"],
    enabled: isNeeded,
    queryFn: async ({ signal }) => unwrap(await api.GET("/api/v1/locale-suggestion", { signal })),
    staleTime: Infinity,
  });
}
