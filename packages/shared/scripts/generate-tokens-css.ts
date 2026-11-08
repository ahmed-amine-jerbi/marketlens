// Writes src/tokens.css from src/tokens.ts so the web app's CSS variables can't drift from the
// shared tokens. Run with `npm run generate:tokens -w @marketlens/shared`; CI fails on a diff.
import { writeFileSync } from "node:fs";

import { fontSizePx, palettes, radiusPx, spacePx } from "../src/tokens.ts";

const kebab = (name: string): string =>
  name.replace(/[A-Z]/g, (letter) => `-${letter.toLowerCase()}`);

const colorVariables = (palette: Readonly<Record<string, string>>): string =>
  Object.entries(palette)
    .map(([role, hex]) => `  --ml-color-${kebab(role)}: ${hex};`)
    .join("\n");

const pxVariables = (prefix: string, scale: Record<string, number>): string =>
  Object.entries(scale)
    .map(([step, px]) => `  --ml-${prefix}-${kebab(step)}: ${(px / 16).toString()}rem;`)
    .join("\n");

const css = `/* Generated from tokens.ts by scripts/generate-tokens-css.ts. Do not edit. */
:root {
  color-scheme: light;
${colorVariables(palettes.light)}
${pxVariables("text", fontSizePx)}
${pxVariables("space", spacePx)}
${pxVariables("radius", radiusPx)}
}

:root[data-theme="dark"] {
  color-scheme: dark;
${colorVariables(palettes.dark)}
}

@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    color-scheme: dark;
${colorVariables(palettes.dark).replaceAll("  --", "    --")}
  }
}
`;

writeFileSync(new URL("../src/tokens.css", import.meta.url), css);
console.log("wrote src/tokens.css");
