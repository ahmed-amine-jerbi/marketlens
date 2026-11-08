/**
 * Design tokens shared by the web (Tailwind theme) and mobile (StyleSheet) apps.
 * Cool neutrals and one cobalt accent; green and red mean only "up" and "down"
 * and are always paired with ▲/▼ or +/−, never used as colour alone.
 */

export const colorSchemes = ["light", "dark"] as const;
export type ColorScheme = (typeof colorSchemes)[number];

export interface Palette {
  background: string;
  surface: string;
  ink: string;
  mutedInk: string;
  divider: string;
  accent: string;
  up: string;
  down: string;
}

export const palettes = {
  light: {
    background: "#F5F7FA",
    surface: "#FFFFFF",
    ink: "#17212C",
    mutedInk: "#5A6775",
    divider: "#DCE2E9",
    accent: "#2B55C6",
    up: "#1A7F51",
    down: "#C43D2E",
  },
  dark: {
    background: "#0E141B",
    surface: "#151D26",
    ink: "#E4E9EF",
    mutedInk: "#94A2B0",
    divider: "#263241",
    accent: "#86A3FF",
    up: "#41BE86",
    down: "#F1735F",
  },
} as const satisfies Record<ColorScheme, Palette>;

export const fontFamily = "IBM Plex Sans";

/** Type scale in px, roughly a 1.25 ratio from the 16 px body size. */
export const fontSizePx = {
  caption: 12,
  small: 14,
  body: 16,
  title: 20,
  heading: 25,
  display: 32,
  hero: 40,
} as const;

/** Spacing steps in px on a 4 px grid. */
export const spacePx = {
  xxs: 4,
  xs: 8,
  sm: 12,
  md: 16,
  lg: 24,
  xl: 32,
  xxl: 48,
  xxxl: 64,
} as const;

export const radiusPx = {
  control: 6,
  panel: 10,
} as const;

export const motion = {
  /** How long a changed price flashes its up/down colour. Skipped under reduced motion. */
  priceFlashMs: 600,
  panelTransitionMs: 180,
} as const;
