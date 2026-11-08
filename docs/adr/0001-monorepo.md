# 0001 — One repository for every part

- **Status:** accepted, 2026-11-08
- **Decision record:** D33 in [the plan](../pipeline/marketlens-v1/plan.md)

## Context

Marketlens has a web app, a mobile app, an API and a forecasting service. The two apps share
design tokens, UI strings, number formatting and the API's types. The project is open source and
read by reviewers who should be able to clone it and run everything.

## Decision

One repository: npm workspaces for `apps/web`, `apps/mobile` and `packages/shared`; a .NET
solution under `services/api`; a uv project under `services/forecaster`. One CI workflow runs every
part's checks.

## Consequences

- A change to the API contract, its generated TypeScript types and both clients lands in one pull
  request.
- Shared tokens and messages can't drift between web and mobile.
- CI runs all jobs on every change; acceptable at this size, and jobs run in parallel.

## Alternatives rejected

- **Separate repositories per part:** contract changes would need coordinated releases and a
  published shared package.
- **Nx or Turborepo:** build caching isn't needed at this size; plain npm workspaces are enough.
