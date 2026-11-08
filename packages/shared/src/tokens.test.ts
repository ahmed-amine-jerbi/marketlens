import { describe, expect, it } from "vitest";

import { colorSchemes, palettes } from "./tokens";

// WCAG 2.2 relative luminance and contrast ratio, https://www.w3.org/TR/WCAG22/#dfn-contrast-ratio
function relativeLuminance(hexColor: string): number {
  const channels = [1, 3, 5].map((start) => parseInt(hexColor.slice(start, start + 2), 16) / 255);
  const [red, green, blue] = channels.map((channel) =>
    channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4,
  ) as [number, number, number];
  return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
}

function contrastRatio(foreground: string, background: string): number {
  const [lighter, darker] = [relativeLuminance(foreground), relativeLuminance(background)].sort(
    (first, second) => second - first,
  ) as [number, number];
  return (lighter + 0.05) / (darker + 0.05);
}

const normalTextMinimum = 4.5;

describe.each(colorSchemes)("%s palette", (scheme) => {
  const palette = palettes[scheme];

  it.each(["ink", "mutedInk", "accent", "up", "down"] as const)(
    "%s is readable as body text on the background and on surfaces",
    (role) => {
      expect(contrastRatio(palette[role], palette.background)).toBeGreaterThanOrEqual(
        normalTextMinimum,
      );
      expect(contrastRatio(palette[role], palette.surface)).toBeGreaterThanOrEqual(
        normalTextMinimum,
      );
    },
  );
});
