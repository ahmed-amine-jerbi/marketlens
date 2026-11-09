import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

test.describe("watchlist", () => {
  test("starts empty, takes a suggestion, and keeps it after a reload", async ({ page }) => {
    await page.goto("/");

    await expect(
      page.getByText("Add currencies, coins or stocks you want to follow."),
    ).toBeVisible();
    await page.getByRole("button", { name: "+ Bitcoin", exact: true }).click();

    const watchlist = page.getByRole("table", { name: "Your watchlist" });
    await expect(watchlist.getByRole("link", { name: /Bitcoin/ })).toBeVisible();
    await expect(watchlist.getByRole("cell").filter({ hasText: /\d/ }).first()).toBeVisible();

    await page.reload();
    await expect(
      page.getByRole("table", { name: "Your watchlist" }).getByRole("link", { name: /Bitcoin/ }),
    ).toBeVisible();
  });
});

test.describe("asset page", () => {
  test("shows a live price, its change and a chart, and switches range", async ({ page }) => {
    await page.goto("/assets/crypto:BTC");

    await expect(page.getByRole("heading", { level: 1, name: /Bitcoin/ })).toBeVisible();
    await expect(page.getByText("past 24 h")).toBeVisible();
    await expect(page.getByRole("img", { name: /Bitcoin price over 1 month/ })).toBeVisible();

    await page.getByRole("radio", { name: "1 day" }).click();
    await expect(page.getByRole("img", { name: /Bitcoin price over 1 day/ })).toBeVisible();
  });

  test("labels stock prices as sample data when no key is set", async ({ page, request }) => {
    const status = (await (await request.get("/api/v1/status")).json()) as { stockData: string };
    test.skip(status.stockData !== "sample", "A Finnhub key is configured.");

    await page.goto("/assets/stock:AAPL");

    await expect(page.getByText("Sample data").first()).toBeVisible();
  });

  test("draws the forecast range with its record, hides it on request and explains the method", async ({
    page,
    request,
  }) => {
    const forecast = (await (await request.get("/api/v1/assets/crypto:BTC/forecast")).json()) as {
      status: string;
    };
    test.skip(forecast.status !== "available", "The scheduler hasn't forecast Bitcoin yet.");
    await page.goto("/assets/crypto:BTC");

    await expect(page.getByRole("img", { name: /Forecast for .*\(80% range\)/ })).toBeVisible();
    await expect(page.getByText(/the real price stayed inside/)).toBeVisible();

    await page.getByRole("checkbox", { name: "Show forecast" }).uncheck();
    await expect(
      page.getByRole("img", { name: /Bitcoin price over 1 month/ }),
    ).not.toHaveAccessibleName(/Forecast for/);
    await expect(page.getByText(/the real price stayed inside/)).toBeHidden();

    await page.getByRole("checkbox", { name: "Show forecast" }).check();
    await page.getByRole("radio", { name: "1 year" }).click();
    await expect(
      page.getByText("The forecast range is drawn on the 1 week, 1 month and 3 month charts."),
    ).toBeVisible();

    await page.getByRole("link", { name: "How this works" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "How forecasts work" })).toBeVisible();
  });

  test("explains an asset Marketlens doesn't track", async ({ page }) => {
    await page.goto("/assets/crypto:NOTACOIN");

    await expect(
      page.getByRole("heading", { name: "Marketlens doesn't track “crypto:NOTACOIN”." }),
    ).toBeVisible();
  });
});

test.describe("converter", () => {
  test.use({ locale: "fr-FR" });

  test("reads a French decimal comma and explains an invalid amount", async ({ page }) => {
    await page.goto("/convert");

    const amount = page.getByLabel("Montant");
    await amount.fill("0,25");
    await expect(page.getByText(/^0,25 BTC =/)).toBeVisible();

    await amount.fill("abc");
    await expect(page.getByText(/Saisissez un montant supérieur à 0/)).toBeVisible();
  });
});

test.describe("languages", () => {
  test.use({ locale: "de-DE" });

  test("follows the browser language and lets the visitor switch", async ({ page }) => {
    await page.goto("/");
    await expect(
      page.getByRole("navigation", { name: "Haupt" }).first().getByRole("link", { name: "Start" }),
    ).toBeVisible();

    await page.getByRole("button", { name: /Einstellungen/ }).click();
    await page.getByLabel("Sprache").selectOption("en");

    await expect(page.getByRole("heading", { name: "Markets today" })).toBeVisible();
    await expect(page.locator("html")).toHaveAttribute("lang", "en");
  });
});

test.describe("navigation", () => {
  test("serves deep links and a not-found page", async ({ page }) => {
    await page.goto("/markets/crypto");
    await expect(page.getByRole("heading", { level: 1, name: "Crypto" })).toBeVisible();

    await page.goto("/no-such-page");
    await expect(page.getByRole("heading", { name: "This page doesn't exist." })).toBeVisible();
  });
});

const pages = [
  "/",
  "/markets/crypto",
  "/markets/currencies",
  "/assets/crypto:BTC",
  "/convert",
  "/data-sources",
  "/how-forecasts-work",
];

for (const colorScheme of ["light", "dark"] as const) {
  test.describe(`accessibility, ${colorScheme} theme`, () => {
    test.use({ colorScheme });

    for (const path of pages) {
      test(`${path} has no serious or critical axe violations`, async ({ page }) => {
        await page.goto(path);
        await page.waitForLoadState("networkidle");

        const results = await new AxeBuilder({ page })
          .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"])
          .analyze();
        const blocking = results.violations.filter(
          (violation) => violation.impact === "serious" || violation.impact === "critical",
        );

        expect(
          blocking.map(
            (violation) =>
              `${violation.id}: ${violation.nodes.map((node) => node.target.join(" ")).join(", ")}`,
          ),
        ).toEqual([]);
      });
    }
  });
}
