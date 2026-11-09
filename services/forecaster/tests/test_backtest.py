from datetime import date

import numpy as np
import pandas as pd
import pytest
from statsforecast.models import Naive

from forecaster.backtest import (
    BASELINE_MODEL,
    ModelScore,
    calibrated_half_widths,
    choose_model,
    score_model,
)
from forecaster.forecast import (
    DailyClose,
    Frequency,
    InsufficientHistoryError,
    backtest_window_count,
    forecast_closes,
)

BASELINE = ModelScore(BASELINE_MODEL, mean_error_pct=2.0, coverage80_pct=80.0)
Z80 = 1.2816
"""Two-sided 80% quantile of a normal distribution."""


def business_day_closes(prices: np.ndarray) -> list[DailyClose]:
    days = pd.bdate_range("2024-01-01", periods=len(prices))
    return [DailyClose(day.date(), float(price)) for day, price in zip(days, prices, strict=True)]


def random_walk_with_volatility_regimes(seed: int) -> np.ndarray:
    """Log prices whose daily volatility switches between calm and turbulent every 250 days."""
    regime_volatility = np.repeat([0.01, 0.035, 0.015, 0.05, 0.02, 0.01], 250)
    returns = np.random.default_rng(seed).normal(scale=regime_volatility)
    log_prices: np.ndarray = np.log(100) + np.cumsum(returns)
    return log_prices


def test_calibrated_ranges_hold_about_as_often_as_they_say_when_volatility_changes() -> None:
    log_prices = random_walk_with_volatility_regimes(seed=11)
    horizon_steps = 7
    inside80: list[bool] = []
    inside95: list[bool] = []

    for cutoff in range(800, len(log_prices) - horizon_steps, horizon_steps):
        history = log_prices[cutoff - 730 : cutoff + 1]
        widths = calibrated_half_widths(history, horizon_steps)
        misses = np.abs(log_prices[cutoff + 1 : cutoff + 1 + horizon_steps] - history[-1])
        inside80.extend(misses <= np.asarray(widths.range80))
        inside95.extend(misses <= np.asarray(widths.range95))

    assert 75 <= np.mean(inside80) * 100 <= 85
    assert np.mean(inside95) * 100 >= 92


def test_ranges_follow_the_current_volatility_rather_than_the_average() -> None:
    turbulent_then_calm = np.concatenate(
        [
            np.random.default_rng(3).normal(scale=0.05, size=500),
            np.random.default_rng(4).normal(scale=0.01, size=230),
        ]
    )
    log_prices = np.log(100) + np.cumsum(turbulent_then_calm)

    widths = calibrated_half_widths(log_prices, horizon_steps=5)

    assert widths.range80[-1] == pytest.approx(Z80 * 0.01 * np.sqrt(5), rel=0.3)
    assert list(widths.range80) == sorted(widths.range80)
    assert all(w80 < w95 for w80, w95 in zip(widths.range80, widths.range95, strict=True))


def test_the_backtest_scores_ranges_calibrated_only_on_the_closes_before_each_window() -> None:
    log_prices = random_walk_with_volatility_regimes(seed=5)[:900]
    frame = pd.DataFrame(
        {
            "unique_id": "asset",
            "ds": pd.date_range("2020-01-01", periods=len(log_prices)),
            "y": log_prices,
        }
    )
    horizon_steps, window_count = 7, 8
    inside: list[bool] = []
    for window in range(window_count, 0, -1):
        cutoff = len(log_prices) - window * horizon_steps - 1
        widths = calibrated_half_widths(log_prices[: cutoff + 1], horizon_steps)
        actual = log_prices[cutoff + 1 : cutoff + 1 + horizon_steps]
        inside.extend(np.abs(actual - log_prices[cutoff]) <= np.asarray(widths.range80))

    score = score_model(
        frame, Naive, freq="D", horizon_steps=horizon_steps, window_count=window_count
    )

    assert score is not None
    assert score.coverage80_pct == pytest.approx(np.mean(inside) * 100)


def test_the_candidate_with_the_lowest_error_is_chosen_when_it_beats_no_change() -> None:
    ets = ModelScore("AutoETS", mean_error_pct=1.5, coverage80_pct=78.0)
    theta = ModelScore("AutoTheta", mean_error_pct=1.2, coverage80_pct=81.0)

    chosen, beats_baseline = choose_model([ets, theta], BASELINE)

    assert chosen == theta
    assert beats_baseline


def test_a_candidate_only_as_good_as_no_change_does_not_beat_it() -> None:
    tied = ModelScore("AutoARIMA", mean_error_pct=2.0, coverage80_pct=90.0)

    chosen, beats_baseline = choose_model([tied], BASELINE)

    assert chosen == BASELINE
    assert not beats_baseline


def test_when_every_candidate_failed_to_fit_the_baseline_is_chosen() -> None:
    chosen, beats_baseline = choose_model([], BASELINE)

    assert chosen == BASELINE
    assert not beats_baseline


@pytest.mark.parametrize(
    ("freq", "horizon_steps", "expected_windows"),
    [("D", 7, 8), ("B", 5, 8), ("D", 3, 19)],
)
def test_backtest_windows_cover_the_requested_weeks_without_overlap(
    freq: Frequency, horizon_steps: int, expected_windows: int
) -> None:
    assert backtest_window_count(freq, horizon_steps, backtest_weeks=8) == expected_windows


def test_a_steady_trend_is_forecast_better_than_no_change_with_nested_ranges() -> None:
    noise = np.random.default_rng(seed=7).normal(scale=0.002, size=400)
    closes = business_day_closes(100 * np.exp(0.01 * np.arange(400) + noise))

    forecast = forecast_closes(closes, freq="B", horizon_steps=5, backtest_weeks=8)

    assert forecast.backtest.beats_baseline
    assert forecast.model != BASELINE_MODEL
    assert forecast.backtest.chosen.mean_error_pct < forecast.backtest.baseline_error_pct
    assert forecast.backtest.window_weeks == 8
    assert [point.date for point in forecast.points] == [
        date(2025, 7, 14),
        date(2025, 7, 15),
        date(2025, 7, 16),
        date(2025, 7, 17),
        date(2025, 7, 18),
    ]
    for point in forecast.points:
        assert 0 < point.low95 <= point.low80 <= point.median <= point.high80 <= point.high95
    assert forecast.points[0].median > closes[-1].close


def test_a_flat_peg_is_never_better_than_no_change_and_its_ranges_have_no_width() -> None:
    closes = business_day_closes(np.full(300, 0.2723))

    forecast = forecast_closes(closes, freq="B", horizon_steps=5, backtest_weeks=8)

    assert not forecast.backtest.beats_baseline
    assert forecast.model == BASELINE_MODEL
    assert forecast.backtest.baseline_error_pct == pytest.approx(0)
    assert forecast.backtest.chosen.coverage80_pct == pytest.approx(100)
    for point in forecast.points:
        assert [point.low95, point.low80, point.median, point.high80, point.high95] == (
            pytest.approx([0.2723] * 5)
        )


def test_too_short_a_series_is_refused_with_the_count_it_needs() -> None:
    closes = business_day_closes(np.linspace(1, 2, 139))

    with pytest.raises(InsufficientHistoryError, match="at least 140"):
        forecast_closes(closes, freq="B", horizon_steps=5, backtest_weeks=8)
