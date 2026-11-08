import type { components, paths } from "@marketlens/shared/api";
import createClient from "openapi-fetch";

export type Asset = components["schemas"]["AssetResponse"];
export type AssetClass = components["schemas"]["AssetClass"];
export type Quote = components["schemas"]["QuoteResponse"];
export type Freshness = components["schemas"]["Freshness"];
export type Conversion = components["schemas"]["ConversionResponse"];
export type History = components["schemas"]["HistoryResponse"];
export type Forecast = components["schemas"]["ForecastResponse"];
export type Status = components["schemas"]["StatusResponse"];
export type LocaleSuggestion = components["schemas"]["LocaleSuggestion"];

/** Same origin in every environment: the Vite dev proxy locally, nginx in Docker. */
export const api = createClient<paths>({ baseUrl: "/" });

/** An RFC 9457 problem the API answered with; `type` is one of docs/api/problems.md. */
export class ApiProblemError extends Error {
  readonly type: string;
  readonly status: number;
  readonly asset: string | undefined;

  constructor(
    problem: { type?: string; title?: string; detail?: string; status?: number; asset?: string },
    status: number,
  ) {
    super(problem.detail ?? problem.title ?? `The API answered ${status.toString()}.`);
    this.name = "ApiProblemError";
    this.type = problem.type ?? "about:blank";
    this.status = status;
    this.asset = problem.asset;
  }

  get isRateUnavailable(): boolean {
    return this.type === "urn:marketlens:problem:rate-unavailable";
  }
}

/** Returns the data of a successful call, or throws an ApiProblemError with the API's own words. */
export function unwrap<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (result.data !== undefined) {
    return result.data;
  }

  const problem = typeof result.error === "object" && result.error !== null ? result.error : {};
  throw new ApiProblemError(problem, result.response.status);
}
