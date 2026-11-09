from datetime import date

import numpy as np
import pandas as pd
import pytest

from forecaster.backtest import BASELINE_MODEL, ModelScore, choose_model
from forecaster.forecast import (
    DailyClose,
    Frequency,
    InsufficientHistoryError,
    backtest_window_count,
    forecast_closes,
)

BASELINE = ModelScore(BASELINE_MODEL, mean_error_pct=2.0, coverage80_pct=80.0)


def business_day_closes(prices: np.ndarray) -> list[DailyClose]:
    days = pd.bdate_range("2024-01-01", periods=len(prices))
    return [DailyClose(day.date(), float(price)) for day, price in zip(days, prices, strict=True)]


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


def test_a_flat_peg_is_never_better_than_no_change() -> None:
    closes = business_day_closes(np.full(300, 0.2723))

    forecast = forecast_closes(closes, freq="B", horizon_steps=5, backtest_weeks=8)

    assert not forecast.backtest.beats_baseline
    assert forecast.model == BASELINE_MODEL
    assert forecast.backtest.baseline_error_pct == pytest.approx(0)
    assert [point.median for point in forecast.points] == pytest.approx([0.2723] * 5)


def test_too_short_a_series_is_refused_with_the_count_it_needs() -> None:
    closes = business_day_closes(np.linspace(1, 2, 139))

    with pytest.raises(InsufficientHistoryError, match="at least 140"):
        forecast_closes(closes, freq="B", horizon_steps=5, backtest_weeks=8)
