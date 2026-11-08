import { describe, expect, it } from "vitest";

import de from "./de.json";
import en from "./en.json";
import fr from "./fr.json";

function keyPaths(messages: object, prefix = ""): string[] {
  return Object.entries(messages).flatMap(([key, value]) =>
    typeof value === "object" && value !== null
      ? keyPaths(value as object, `${prefix}${key}.`)
      : [`${prefix}${key}`],
  );
}

function placeholders(text: string): string[] {
  return [...text.matchAll(/\{\{(\w+)\}\}/g)].map((match) => match[1] ?? "").sort();
}

describe.each([
  ["fr", fr],
  ["de", de],
] as const)("%s messages", (_language, messages) => {
  it("have exactly the English keys", () => {
    expect(keyPaths(messages).sort()).toEqual(keyPaths(en).sort());
  });

  it("use the same placeholders as English", () => {
    const flat = (source: object) =>
      Object.fromEntries(
        keyPaths(source).map((path) => [
          path,
          path
            .split(".")
            .reduce<unknown>((node, key) => (node as Record<string, unknown>)[key], source),
        ]),
      );
    const english = flat(en);
    const translated = flat(messages);

    for (const [path, text] of Object.entries(english)) {
      expect(placeholders(String(translated[path])), path).toEqual(placeholders(String(text)));
    }
  });
});
