# 0005 — A stateless Python forecaster called by the API

- **Status:** accepted, 2026-11-08

## Context

Forecasts need Python's time-series libraries. Price forecasts are often no better than "no
change", so the product promises ranges, a measured track record and an honest "not better than
the baseline" state instead of a single predicted price.

## Decision

A FastAPI service, `services/forecaster`, with one internal endpoint, `POST /v1/forecasts`: the API
sends an asset's daily closes, the service returns a median with 80% and 95% bands and a rolling
backtest against the "no change" baseline. Models come from `statsforecast` (AutoETS, AutoTheta,
AutoARIMA on log prices), chosen per asset by backtest. The service has no database and no public
port; the API schedules runs, stores results and serves them.

## Consequences

- The forecaster can be tested with fixed JSON fixtures; the same fixtures back contract tests on
  the API side.
- Forecasts are computed once a day per asset, not per request, so the service can be slow
  without affecting users.
- Machine-learning models can replace the statistical ones later behind the same contract.

## Alternatives rejected

- **Forecasting inside .NET:** the statistical libraries and their backtesting tools are mature in
  Python.
- **Giving the forecaster its own database:** it would duplicate the API's candles and create a
  second owner for price history.
