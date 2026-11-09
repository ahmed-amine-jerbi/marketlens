import { describe, expect, it } from "vitest";

import { toChartPoints } from "./PriceChart";

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
