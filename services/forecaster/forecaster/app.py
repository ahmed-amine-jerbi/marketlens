"""HTTP entry point of the forecaster. Reachable only on the internal Docker network.

`POST /v1/forecasts` takes an asset's daily closes and returns a median with 80% and 95% ranges
plus the backtest that decides whether the API may show it (ADR 0005). Prices and percentages
travel as decimal strings, like everywhere else in Marketlens (D39, D53).
"""

import itertools
import logging
from datetime import date
from decimal import Decimal
from typing import Annotated, Self

from fastapi import FastAPI, HTTPException, status
from pydantic import BaseModel, ConfigDict, Field, PlainSerializer, model_validator
from pydantic.alias_generators import to_camel

from forecaster.forecast import (
    DailyClose,
    Forecast,
    Frequency,
    InsufficientHistoryError,
    forecast_closes,
)

PRICE_SIGNIFICANT_DIGITS = 10
PERCENT_DECIMAL_PLACES = 2

logger = logging.getLogger(__name__)

# Plain notation: Python writes small decimals as "1.2E-7", which the API's decimal reader refuses.
DecimalText = Annotated[Decimal, PlainSerializer(lambda value: format(value, "f"), return_type=str)]


class CamelModel(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True, extra="forbid")


class DailyCloseInput(CamelModel):
    date: date
    close: Decimal = Field(gt=0)


class ForecastRequest(CamelModel):
    asset_key: str = Field(pattern=r"^[a-z]+:[A-Z0-9.\-]{1,15}$", description="For logs only.")
    closes: list[DailyCloseInput] = Field(min_length=1, max_length=5000)
    freq: Frequency
    horizon_steps: int = Field(ge=1, le=30)
    backtest_weeks: int = Field(ge=1, le=52)

    @model_validator(mode="after")
    def closes_are_in_date_order_on_the_series_calendar(self) -> Self:
        dates = [close.date for close in self.closes]
        if any(earlier >= later for earlier, later in itertools.pairwise(dates)):
            raise ValueError("closes must be in ascending date order with no repeated date")
        if self.freq == "B" and any(day.weekday() >= 5 for day in dates):
            raise ValueError("closes for freq B must be weekdays only")
        return self


class ForecastPointOutput(CamelModel):
    date: date
    median: DecimalText
    low80: DecimalText
    high80: DecimalText
    low95: DecimalText
    high95: DecimalText


class BacktestOutput(CamelModel):
    window_weeks: int
    coverage80_pct: DecimalText
    mean_error_pct: DecimalText
    baseline_error_pct: DecimalText
    beats_baseline: bool


class ForecastResponse(CamelModel):
    model: str
    points: list[ForecastPointOutput]
    backtest: BacktestOutput


app = FastAPI(title="Marketlens forecaster", version="0.1.0")


@app.get("/health")
def read_health() -> dict[str, str]:
    """Liveness probe for container health checks."""
    return {"status": "healthy"}


@app.post("/v1/forecasts", response_model_by_alias=True)
def create_forecast(request: ForecastRequest) -> ForecastResponse:
    """Backtests the candidate models on the closes and forecasts the next `horizonSteps`.

    Synchronous on purpose: the work is CPU-bound, and FastAPI runs it in a worker thread.
    """
    closes = [DailyClose(close.date, float(close.close)) for close in request.closes]
    try:
        forecast = forecast_closes(
            closes,
            freq=request.freq,
            horizon_steps=request.horizon_steps,
            backtest_weeks=request.backtest_weeks,
        )
    except InsufficientHistoryError as error:
        raise HTTPException(status.HTTP_422_UNPROCESSABLE_CONTENT, detail=str(error)) from error

    logger.info(
        "Forecast for %s: %s, beats no change: %s",
        request.asset_key,
        forecast.model,
        forecast.backtest.beats_baseline,
    )
    return _to_response(forecast)


def _to_response(forecast: Forecast) -> ForecastResponse:
    backtest = forecast.backtest
    return ForecastResponse(
        model=forecast.model,
        points=[
            ForecastPointOutput(
                date=point.date,
                median=_price(point.median),
                low80=_price(point.low80),
                high80=_price(point.high80),
                low95=_price(point.low95),
                high95=_price(point.high95),
            )
            for point in forecast.points
        ],
        backtest=BacktestOutput(
            window_weeks=backtest.window_weeks,
            coverage80_pct=_percent(backtest.chosen.coverage80_pct),
            mean_error_pct=_percent(backtest.chosen.mean_error_pct),
            baseline_error_pct=_percent(backtest.baseline_error_pct),
            beats_baseline=backtest.beats_baseline,
        ),
    )


def _price(value: float) -> Decimal:
    return Decimal(f"{value:.{PRICE_SIGNIFICANT_DIGITS}g}")


def _percent(value: float) -> Decimal:
    return round(Decimal(value), PERCENT_DECIMAL_PLACES)
