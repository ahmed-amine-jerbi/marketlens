"""Rolling-origin backtests, calibrated ranges, and the rule that decides whether a forecast is
worth showing.

Each model is scored on windows that end before the latest close: it is fitted on the closes up
to a cut-off, forecasts the next steps, and is compared with what actually happened. The "no
change" forecast (the last close carried forward) is the baseline every model must beat (D54).
The ranges are not the models' own intervals but calibrated ones (`calibrated_half_widths`), and
the backtest scores those, so the track record measures the ranges the app shows.
"""

import logging
import math
from collections.abc import Callable, Sequence
from dataclasses import dataclass
from typing import Any

import numpy as np
import pandas as pd
from statsforecast import StatsForecast

BASELINE_MODEL = "Naive"

EWMA_LAMBDA = 0.94
"""Weight of the previous day's variance in the volatility estimate (RiskMetrics' daily value)."""

VOLATILITY_FLOOR = 1e-6
"""Smallest daily log volatility used, so a flat peg gets zero-width, finite ranges."""

_VOLATILITY_SEED_RETURNS = 30
"""Returns averaged to start the volatility estimate before the first close it covers."""

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class ModelScore:
    """How one model did over every backtest window."""

    model: str
    mean_error_pct: float
    coverage80_pct: float


@dataclass(frozen=True)
class Backtest:
    """The backtest summary returned with a forecast."""

    window_weeks: int
    chosen: ModelScore
    baseline_error_pct: float
    beats_baseline: bool


@dataclass(frozen=True)
class RangeHalfWidths:
    """How far each step's ranges reach either side of the median, in log price (step 1 first)."""

    range80: tuple[float, ...]
    range95: tuple[float, ...]


def calibrated_half_widths(log_closes: np.ndarray, horizon_steps: int) -> RangeHalfWidths:
    """Sizes the 80% and 95% ranges from how far this asset's price really moved (D9, D20).

    Every past day is an origin: its "no change" miss after `k` steps, divided by the volatility
    known on that day and by √k, is one score. The ranges are the 80% and 95% finite-sample
    quantiles of all scores, scaled back by today's volatility and √k. So on the asset's own
    history they hold as often as they say, and they widen or narrow as the asset becomes more or
    less volatile. Uses only the closes it is given: callers pass the closes up to a cut-off.
    """
    volatility = _ewma_volatility(log_closes)
    steps = np.arange(1, horizon_steps + 1)
    origins = np.arange(len(log_closes) - horizon_steps)
    misses = np.abs(log_closes[origins[:, None] + steps] - log_closes[origins][:, None])
    scores = (misses / (volatility[origins][:, None] * np.sqrt(steps))).ravel()

    scale = volatility[-1] * np.sqrt(steps)
    return RangeHalfWidths(
        range80=tuple(float(width) for width in _conformal_quantile(scores, 0.80) * scale),
        range95=tuple(float(width) for width in _conformal_quantile(scores, 0.95) * scale),
    )


def _ewma_volatility(log_closes: np.ndarray) -> np.ndarray:
    """Daily log-return volatility known at each close, from an exponentially weighted variance."""
    returns = np.diff(log_closes)
    variance = np.empty(len(log_closes))
    variance[0] = np.mean(returns[:_VOLATILITY_SEED_RETURNS] ** 2)
    for day in range(1, len(log_closes)):
        variance[day] = EWMA_LAMBDA * variance[day - 1] + (1 - EWMA_LAMBDA) * returns[day - 1] ** 2
    return np.maximum(np.sqrt(variance), VOLATILITY_FLOOR)


def _conformal_quantile(scores: np.ndarray, level: float) -> float:
    """The score at rank ⌈(n + 1)·level⌉, which keeps the coverage promise for a finite sample."""
    rank = min(math.ceil((len(scores) + 1) * level), len(scores))
    return float(np.partition(scores, rank - 1)[rank - 1])


def choose_model(candidates: Sequence[ModelScore], baseline: ModelScore) -> tuple[ModelScore, bool]:
    """Picks the lowest-error candidate, but only if it is strictly better than "no change".

    Returns the chosen score and whether it beats the baseline. When no candidate beats it, the
    baseline itself is chosen: the range is then centred on the last close, and the app says the
    models were no better than assuming the price stays where it is (D57).
    """
    if not candidates:
        return baseline, False

    best = min(candidates, key=lambda score: score.mean_error_pct)
    if best.mean_error_pct < baseline.mean_error_pct:
        return best, True

    return baseline, False


def score_model(
    log_closes: pd.DataFrame,
    model_factory: Callable[[], Any],
    *,
    freq: str,
    horizon_steps: int,
    window_count: int,
) -> ModelScore | None:
    """Backtests one model on log closes (`unique_id`, `ds`, `y`); None if it fails to fit.

    Windows are spaced `horizon_steps` apart, so they don't overlap, and the model is refitted at
    every cut-off. Each window's 80% range is calibrated on the closes up to its cut-off only.
    Errors and coverage are measured on prices, not on logs.
    """
    model = model_factory()
    try:
        windows = StatsForecast(models=[model], freq=freq).cross_validation(
            h=horizon_steps,
            df=log_closes,
            n_windows=window_count,
            step_size=horizon_steps,
        )
    # statsforecast raises a range of numeric errors for series a model can't fit (flat pegs,
    # degenerate fits). Such a model is left out of the choice, and the reason is logged.
    except Exception:
        logger.warning("Backtest of %s failed; model left out", model.alias, exc_info=True)
        return None

    return _score_windows(as_pandas(windows), log_closes, model.alias, horizon_steps)


def as_pandas(frame: object) -> pd.DataFrame:
    """Narrows a statsforecast result (typed pandas-or-polars) to the pandas frame it returns."""
    if not isinstance(frame, pd.DataFrame):
        raise TypeError(f"Expected a pandas DataFrame, got {type(frame).__name__}.")
    return frame


def _score_windows(
    windows: pd.DataFrame, log_closes: pd.DataFrame, model: str, horizon_steps: int
) -> ModelScore | None:
    windows = windows.sort_values(["cutoff", "ds"])
    log_median = windows[model].to_numpy(dtype=float)
    half_width80 = np.concatenate(
        [
            calibrated_half_widths(
                log_closes.loc[log_closes["ds"] <= cutoff, "y"].to_numpy(dtype=float), horizon_steps
            ).range80
            for cutoff in windows["cutoff"].unique()
        ]
    )

    actual = np.exp(windows["y"].to_numpy(dtype=float))
    median = np.exp(log_median)
    low80 = np.exp(log_median - half_width80)
    high80 = np.exp(log_median + half_width80)

    mean_error_pct = float(np.mean(np.abs(median - actual) / actual) * 100)
    coverage80_pct = float(np.mean((actual >= low80) & (actual <= high80)) * 100)
    if not (math.isfinite(mean_error_pct) and math.isfinite(coverage80_pct)):
        logger.warning("Backtest of %s produced non-finite values; model left out", model)
        return None

    return ModelScore(model, mean_error_pct, coverage80_pct)
