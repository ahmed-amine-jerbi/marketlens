"""Rolling-origin backtests and the rule that decides whether a forecast is worth showing.

Each model is scored on windows that end before the latest close: it is fitted on the closes up
to a cut-off, forecasts the next steps, and is compared with what actually happened. The "no
change" forecast (the last close carried forward) is the baseline every model must beat (D54).
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
COVERAGE_LEVEL = 80

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


def choose_model(candidates: Sequence[ModelScore], baseline: ModelScore) -> tuple[ModelScore, bool]:
    """Picks the lowest-error candidate, but only if it is strictly better than "no change".

    Returns the chosen score and whether it beats the baseline. When no candidate beats it, the
    baseline itself is chosen, so the caller never shows a forecast that history says is worse
    than assuming the price stays where it is.
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
    every cut-off. Errors and coverage are measured on prices, not on logs.
    """
    model = model_factory()
    try:
        windows = StatsForecast(models=[model], freq=freq).cross_validation(
            h=horizon_steps,
            df=log_closes,
            n_windows=window_count,
            step_size=horizon_steps,
            level=[COVERAGE_LEVEL],
        )
    # statsforecast raises a range of numeric errors for series a model can't fit (flat pegs,
    # degenerate fits). Such a model is left out of the choice, and the reason is logged.
    except Exception:
        logger.warning("Backtest of %s failed; model left out", model.alias, exc_info=True)
        return None

    return _score_windows(as_pandas(windows), model.alias)


def as_pandas(frame: object) -> pd.DataFrame:
    """Narrows a statsforecast result (typed pandas-or-polars) to the pandas frame it returns."""
    if not isinstance(frame, pd.DataFrame):
        raise TypeError(f"Expected a pandas DataFrame, got {type(frame).__name__}.")
    return frame


def _score_windows(windows: pd.DataFrame, model: str) -> ModelScore | None:
    actual = np.exp(windows["y"].to_numpy(dtype=float))
    median = np.exp(windows[model].to_numpy(dtype=float))
    low80 = np.exp(windows[f"{model}-lo-{COVERAGE_LEVEL}"].to_numpy(dtype=float))
    high80 = np.exp(windows[f"{model}-hi-{COVERAGE_LEVEL}"].to_numpy(dtype=float))

    mean_error_pct = float(np.mean(np.abs(median - actual) / actual) * 100)
    coverage80_pct = float(np.mean((actual >= low80) & (actual <= high80)) * 100)
    if not (math.isfinite(mean_error_pct) and math.isfinite(coverage80_pct)):
        logger.warning("Backtest of %s produced non-finite values; model left out", model)
        return None

    return ModelScore(model, mean_error_pct, coverage80_pct)
