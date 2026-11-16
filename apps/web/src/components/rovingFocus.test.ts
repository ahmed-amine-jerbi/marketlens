import { describe, expect, it } from "vitest";

import { rovingIndex } from "./rovingFocus";

describe("rovingIndex", () => {
  it("moves to the next option and wraps from the last to the first", () => {
    expect(rovingIndex("ArrowRight", 0, 3)).toBe(1);
    expect(rovingIndex("ArrowDown", 2, 3)).toBe(0);
  });

  it("moves to the previous option and wraps from the first to the last", () => {
    expect(rovingIndex("ArrowLeft", 1, 3)).toBe(0);
    expect(rovingIndex("ArrowUp", 0, 3)).toBe(2);
  });

  it("jumps to the ends with Home and End", () => {
    expect(rovingIndex("Home", 2, 6)).toBe(0);
    expect(rovingIndex("End", 0, 6)).toBe(5);
  });

  it("leaves other keys to the browser", () => {
    expect(rovingIndex("Tab", 1, 3)).toBeUndefined();
    expect(rovingIndex("Enter", 1, 3)).toBeUndefined();
  });
});
