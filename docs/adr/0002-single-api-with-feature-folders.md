# 0002 — One API project organised by feature folders

- **Status:** accepted, 2026-11-08

## Context

The API owns ingestion, quotes, conversions, history, forecasts, accounts, watchlists, alerts and
news. A common .NET template splits such an app into Domain, Application and Infrastructure
projects with MediatR handlers. The project rules ask for one owner per responsibility and no
layers that only forward calls.

## Decision

A single `Marketlens.Api` project with one folder per feature (`MarketData/`, `Quotes/`,
`Conversions/`, `History/`, `Forecasts/`, `News/`, `Accounts/`, `Watchlists/`, `Alerts/`,
`Locale/`, `Status/`) plus `Data/` for the DbContext, entities and migrations. Endpoints are minimal
APIs that call the feature's own classes directly. Pure rules (for example `ConversionCalculator`)
have no I/O and are unit-tested.

## Consequences

- A reader finds a feature in one folder.
- Nothing stops one feature reaching into another's internals; reviews and tests hold that line.
- If the API ever needs to split into several deployables, the feature folders are the seams.

## Alternatives rejected

- **Clean architecture projects + MediatR:** more files and indirection per feature with one
  implementation behind each interface; no second deployable to justify it.
- **Controllers:** minimal APIs are lighter and are the current ASP.NET Core default.
