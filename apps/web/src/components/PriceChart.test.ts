import { describe, expect, it } from "vitest";

import { compareCloses, nearestHistoryIndex, toChartPoints } from "./PriceChart";

const history = [
  { t: "2026-11-06T00:00:00Z", close: "75100.5" },
  { t: "2026-11-07T00:00:00Z", close: "75850.84" },
];

const forecastDay = (t: string, median: string) => ({
  t,
  median,
  low80: "70000",
  high80: "80000",
  low95: "68000",
  high95: "83000",
});

describe("toChartPoints", () => {
  it("keeps history as it is when there is no forecast", () => {
    expect(toChartPoints(history, [])).toEqual([
      { time: Date.parse("2026-11-06T00:00:00Z"), close: 75100.5 },
      { time: Date.parse("2026-11-07T00:00:00Z"), close: 75850.84 },
    ]);
  });

  it("opens the range at the last close so the line hands over without a gap", () => {
    const points = toChartPoints(history, [forecastDay("2026-11-08T00:00:00Z", "75848.97")]);

    expect(points[1]).toEqual({
      time: Date.parse("2026-11-07T00:00:00Z"),
      close: 75850.84,
      median: 75850.84,
      band80: [75850.84, 75850.84],
      band95: [75850.84, 75850.84],
    });
    expect(points[2]).toEqual({
      time: Date.parse("2026-11-08T00:00:00Z"),
      median: 75848.97,
      band80: [70000, 80000],
      band95: [68000, 83000],
    });
  });

  it("drops forecast days the history already covers", () => {
    const points = toChartPoints(history, [
      forecastDay("2026-11-07T00:00:00Z", "75000"),
      forecastDay("2026-11-08T00:00:00Z", "75848.97"),
    ]);

    expect(points.map((point) => point.time)).toEqual([
      Date.parse("2026-11-06T00:00:00Z"),
      Date.parse("2026-11-07T00:00:00Z"),
      Date.parse("2026-11-08T00:00:00Z"),
    ]);
  });
});

describe("nearestHistoryIndex", () => {
  const days = [
    { t: "2026-11-06T00:00:00Z", close: "1" },
    { t: "2026-11-07T00:00:00Z", close: "2" },
    { t: "2026-11-08T00:00:00Z", close: "3" },
  ];

  it("picks the closest past close", () => {
    expect(nearestHistoryIndex(days, Date.parse("2026-11-07T05:00:00Z"))).toBe(1);
    expect(nearestHistoryIndex(days, Date.parse("2026-11-07T20:00:00Z"))).toBe(2);
  });

  it("stops at the last close, so forecast days are never picked", () => {
    expect(nearestHistoryIndex(days, Date.parse("2026-11-12T00:00:00Z"))).toBe(2);
  });

  it("stops at the first close before the chart starts", () => {
    expect(nearestHistoryIndex(days, Date.parse("2026-10-01T00:00:00Z"))).toBe(0);
  });

  it("works with a single point and reports an empty history", () => {
    expect(nearestHistoryIndex(days.slice(0, 1), Date.parse("2026-11-09T00:00:00Z"))).toBe(0);
    expect(nearestHistoryIndex([], 0)).toBe(-1);
  });
});

describe("compareCloses", () => {
  it("gives the exact difference and the percent change between two decimal prices", () => {
    expect(compareCloses("75100.5", "75850.84")).toEqual({
      difference: "750.34",
      changePct: "0.9991",
    });
  });

  it("is negative for a fall and keeps tiny prices exact", () => {
    expect(compareCloses("0.000003473", "0.000003428")).toEqual({
      difference: "-0.000000045",
      changePct: "-1.2957",
    });
  });

  it("is zero when both points have the same price", () => {
    expect(compareCloses("64208.31", "64208.310")).toEqual({ difference: "0", changePct: "0" });
  });

  it("rounds the percent half away from zero at four decimals", () => {
    expect(compareCloses("3", "4")).toEqual({ difference: "1", changePct: "33.3333" });
    expect(compareCloses("8", "7.99999")).toEqual({ difference: "-0.00001", changePct: "-0.0001" });
  });
});
