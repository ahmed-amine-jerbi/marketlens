import { describe, expect, it } from "vitest";

import { detectLanguage } from "./detectLanguage";
import {
  changeDirection,
  formatAge,
  formatChangePct,
  formatDate,
  formatPrice,
  parseAmount,
} from "./format";

// Intl uses non-breaking spaces as group separators in French; normalise them to compare.
const plainSpaces = (text: string) => text.replace(/[\u00A0\u202F]/g, " ");

describe("formatPrice", () => {
  it.each([
    ["87412.55", "en", "87,412.55"],
    ["87412.55", "fr", "87 412,55"],
    ["87412.55", "de", "87.412,55"],
    ["1.0832", "en", "1.0832"],
    ["231.4", "en", "231.40"],
    ["115.4223", "fr", "115,42"],
    ["9.5467", "en", "9.5467"],
    ["0.00001234", "en", "0.00001234"],
    ["0.3341", "de", "0,3341"],
  ] as const)("formats %s in %s as %s", (value, language, expected) => {
    expect(plainSpaces(formatPrice(value, language))).toBe(expected);
  });

  it("shows input it can't read as it came", () => {
    expect(formatPrice("n/a", "en")).toBe("n/a");
  });
});

describe("formatChangePct", () => {
  it("signs rises and falls, using a true minus sign", () => {
    expect(formatChangePct("1.8", "en")).toBe("+1.80%");
    expect(formatChangePct("-0.8398", "en")).toBe("\u22120.84%");
    expect(plainSpaces(formatChangePct("-0.8398", "fr"))).toBe("\u22120,84 %");
  });

  it("leaves a missing change empty", () => {
    expect(formatChangePct(null, "en")).toBe("");
  });
});

describe("changeDirection", () => {
  it.each([
    ["0.42", "up"],
    ["-0.1", "down"],
    ["0", "flat"],
    [null, "flat"],
  ] as const)("reads %s as %s", (change, direction) => {
    expect(changeDirection(change)).toBe(direction);
  });
});

describe("formatAge", () => {
  const now = new Date("2026-11-08T16:00:00Z");

  it.each([
    ["2026-11-08T15:59:58Z", "en", "2 sec. ago"],
    ["2026-11-08T15:55:00Z", "en", "5 min. ago"],
    ["2026-11-08T13:00:00Z", "fr", "il y a 3 h"],
    ["2026-11-05T16:00:00Z", "de", "vor 3 Tagen"],
  ] as const)("says how long ago %s was in %s", (asOfUtc, language, expected) => {
    expect(plainSpaces(formatAge(asOfUtc, now, language))).toBe(expected);
  });
});

describe("formatDate", () => {
  it("writes the date the language's way", () => {
    expect(formatDate("2026-11-08T00:00:00Z", "en")).toBe("Nov 8, 2026");
    expect(plainSpaces(formatDate("2026-11-08T00:00:00Z", "fr"))).toBe("8 nov. 2026");
  });
});

describe("parseAmount", () => {
  it.each([
    ["0.25", "en", "0.25"],
    ["0,25", "fr", "0.25"],
    ["1 234,5", "fr", "1234.5"],
    ["1.234,5", "de", "1234.5"],
    ["1,234.5", "en", "1234.5"],
    [" 007 ", "en", "7"],
  ] as const)("reads %j in %s as %s", (text, language, expected) => {
    expect(parseAmount(text, language)).toBe(expected);
  });

  it.each([
    ["", "en"],
    ["0", "en"],
    ["-3", "en"],
    ["abc", "en"],
    ["1.2.3", "en"],
  ] as const)("refuses %j in %s", (text, language) => {
    expect(parseAmount(text, language)).toBeNull();
  });
});

describe("detectLanguage", () => {
  it("lets the saved choice win", () => {
    expect(detectLanguage({ saved: "de", deviceLanguages: ["fr-FR"], suggested: "en" })).toBe("de");
  });

  it("uses the first supported device language", () => {
    expect(
      detectLanguage({ saved: null, deviceLanguages: ["ar-TN", "fr-TN", "en"], suggested: null }),
    ).toBe("fr");
  });

  it("falls back to the server suggestion, then English", () => {
    expect(detectLanguage({ saved: null, deviceLanguages: ["ja-JP"], suggested: "de" })).toBe("de");
    expect(detectLanguage({ saved: "es", deviceLanguages: [], suggested: null })).toBe("en");
  });
});
