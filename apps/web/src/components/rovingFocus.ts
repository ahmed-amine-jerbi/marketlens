/**
 * The option an arrow key, Home or End moves to in a tab list or radio group (WAI-ARIA roving
 * focus: arrows wrap around, Home and End jump to the ends). Undefined for any other key, which the
 * caller leaves to the browser.
 */
export function rovingIndex(key: string, currentIndex: number, count: number): number | undefined {
  switch (key) {
    case "ArrowRight":
    case "ArrowDown":
      return (currentIndex + 1) % count;
    case "ArrowLeft":
    case "ArrowUp":
      return (currentIndex - 1 + count) % count;
    case "Home":
      return 0;
    case "End":
      return count - 1;
    default:
      return undefined;
  }
}
