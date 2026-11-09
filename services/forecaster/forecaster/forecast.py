"""Price forecasts: statistical models on log daily closes, chosen per asset by backtest (D29).

The chosen model gives the median; the 80% and 95% ranges around it are calibrated on the
asset's own past moves (`calibrated_half_widths`). Working on logs keeps every range positive and
makes a percentage move the same size at any price level. Turning a log forecast back into a price
gives the median, not the mean.
"""

import math
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass
from datetime import date
from typing import Any, Literal

import numpy as np
import pandas as pd
from statsforecast import StatsForecast
from statsforecast.models import AutoARIMA, AutoETS, AutoTheta, Naive

from forecaster.backtest import (
    BASELINE_MODEL,
    Backtest,
    as_pandas,
    calibrated_half_widths,
    choose_model,
    score_model,
)

Frequency = Literal["D", "B"]
"""`D`: every calendar day (crypto trades daily). `B`: business days (stocks, ETFs, fiat)."""

CANDIDATE_MODELS: Mapping[str, Callable[[], Any]] = {
    "AutoETS": AutoETS,
    "AutoTheta": AutoTheta,
    "AutoARIMA": AutoARIMA,
}

MIN_TRAINING_CLOSES = 100
"""Closes a model needs before the first backtest window, on top of the windows themselves."""

_MODEL_FACTORIES: Mapping[str, Callable[[], Any]] = {**CANDIDATE_MODELS, BASELINE_MODEL: Naive}
_STEPS_PER_WEEK: Mapping[Frequency, int] = {"D": 7, "B": 5}
_SERIES_ID = "asset"


class InsufficientHistoryError(ValueError):
    """The series is too short to backtest and forecast it."""


@dataclass(frozen=True)
class DailyClose:
    date: date
    close: float


@dataclass(frozen=True)
class ForecastPoint:
    """One future step: the median and the 80% and 95% ranges, in the series' currency."""

    date: date
    median: float
    low80: float
    high80: float
    low95: float
    high95: float


@dataclass(frozen=True)
class Forecast:
    model: str
    points: list[ForecastPoint]
    backtest: Backtest


def backtest_window_count(freq: Frequency, horizon_steps: int, backtest_weeks: int) -> int:
    """How many non-overlapping windows of `horizon_steps` cover `backtest_weeks` weeks."""
    return math.ceil(backtest_weeks * _STEPS_PER_WEEK[freq] / horizon_steps)


def forecast_closes(
    closes: Sequence[DailyClose],
    *,
    freq: Frequency,
    horizon_steps: int,
    backtest_weeks: int,
) -> Forecast:
    """Backtests every candidate model, picks one by the baseline rule, and forecasts with it.

    `closes` must be in date order with positive prices; for `B`, weekdays only. When no
    candidate beats "no change", the forecast is the baseline's and `beats_baseline` is false.

    Raises:
        InsufficientHistoryError: fewer closes than the training minimum plus the backtest windows.
    """
    window_count = backtest_window_count(freq, horizon_steps, backtest_weeks)
    required_count = MIN_TRAINING_CLOSES + window_count * horizon_steps
    if len(closes) < required_count:
        raise InsufficientHistoryError(
            f"Got {len(closes)} closes; at least {required_count} are needed to backtest "
            f"{backtest_weeks} weeks and forecast {horizon_steps} steps."
        )

    log_closes = _to_log_frame(closes)
    candidate_scores = [
        score
        for factory in CANDIDATE_MODELS.values()
        if (
            score := score_model(
                log_closes,
                factory,
                freq=freq,
                horizon_steps=horizon_steps,
                window_count=window_count,
            )
        )
        is not None
    ]
    baseline_score = score_model(
        log_closes, Naive, freq=freq, horizon_steps=horizon_steps, window_count=window_count
    )
    if baseline_score is None:
        raise RuntimeError("The no-change baseline could not be backtested on a valid series.")

    chosen, beats_baseline = choose_model(candidate_scores, baseline_score)
    points = _forecast_points(log_closes, _MODEL_FACTORIES[chosen.model], freq, horizon_steps)
    backtest = Backtest(
        window_weeks=backtest_weeks,
        chosen=chosen,
        baseline_error_pct=baseline_score.mean_error_pct,
        beats_baseline=beats_baseline,
    )
    return Forecast(model=chosen.model, points=points, backtest=backtest)


def _to_log_frame(closes: Sequence[DailyClose]) -> pd.DataFrame:
    return pd.DataFrame(
        {
            "unique_id": _SERIES_ID,
            "ds": pd.to_datetime([close.date for close in closes]),
            "y": np.log([close.close for close in closes]),
        }
    )


def _forecast_points(
    log_closes: pd.DataFrame,
    model_factory: Callable[[], Any],
    freq: Frequency,
    horizon_steps: int,
) -> list[ForecastPoint]:
    model = model_factory()
    future = as_pandas(
        StatsForecast(models=[model], freq=freq).forecast(h=horizon_steps, df=log_closes)
    )
    log_median = future[model.alias].to_numpy(dtype=float)
    half_widths = calibrated_half_widths(log_closes["y"].to_numpy(dtype=float), horizon_steps)
    range80 = np.asarray(half_widths.range80)
    range95 = np.asarray(half_widths.range95)

    def price(log_price: np.ndarray) -> list[float]:
        return [float(value) for value in np.exp(log_price)]

    return [
        ForecastPoint(day.date(), median, low80, high80, low95, high95)
        for day, median, low80, high80, low95, high95 in zip(
            future["ds"],
            price(log_median),
            price(log_median - range80),
            price(log_median + range80),
            price(log_median - range95),
            price(log_median + range95),
            strict=True,
        )
    ]
