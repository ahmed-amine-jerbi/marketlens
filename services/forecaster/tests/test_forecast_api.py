"""`POST /v1/forecasts` against the contract fixtures that the API's contract tests also read."""

import json
from datetime import date, timedelta
from pathlib import Path
from typing import Any

import pytest
from fastapi.testclient import TestClient

from forecaster.app import app

CONTRACT = Path(__file__).parent / "contract"


def read_contract(name: str) -> dict[str, Any]:
    contract: dict[str, Any] = json.loads((CONTRACT / name).read_text(encoding="utf-8"))
    return contract


BTC_REQUEST = read_contract("forecast-request-btc.json")
BTC_CLOSES: list[dict[str, str]] = BTC_REQUEST["closes"]

client = TestClient(app)


@pytest.fixture(scope="module")
def btc_response() -> dict[str, Any]:
    response = client.post("/v1/forecasts", json=BTC_REQUEST)
    assert response.status_code == 200
    body: dict[str, Any] = response.json()
    return body


def shape(value: Any) -> Any:
    """The keys and JSON types of a value, ignoring the numbers themselves."""
    if isinstance(value, dict):
        return {key: shape(item) for key, item in value.items()}
    if isinstance(value, list):
        return [shape(value[0])] if value else []
    return type(value).__name__


def test_the_response_has_the_shape_of_the_recorded_contract(btc_response: dict[str, Any]) -> None:
    assert shape(btc_response) == shape(read_contract("forecast-response-btc.json"))


def test_points_cover_the_days_after_the_last_close_with_prices_as_decimal_strings(
    btc_response: dict[str, Any],
) -> None:
    last_close = date.fromisoformat(BTC_CLOSES[-1]["date"])

    points = btc_response["points"]

    assert [point["date"] for point in points] == [
        (last_close + timedelta(days=step)).isoformat() for step in range(1, 8)
    ]
    for point in points:
        low95, low80, median, high80, high95 = (
            float(point[field]) for field in ("low95", "low80", "median", "high80", "high95")
        )
        assert 0 < low95 <= low80 <= median <= high80 <= high95
        assert "E" not in point["median"].upper()


def test_the_backtest_reports_eight_weeks_against_no_change(btc_response: dict[str, Any]) -> None:
    backtest = btc_response["backtest"]

    assert backtest["windowWeeks"] == 8
    assert 0 <= float(backtest["coverage80Pct"]) <= 100
    assert backtest["beatsBaseline"] == (btc_response["model"] != "Naive")
    assert float(backtest["meanErrorPct"]) <= float(backtest["baselineErrorPct"])


@pytest.mark.parametrize(
    "changes",
    [
        pytest.param({"closes": list(reversed(BTC_CLOSES))}, id="closes out of order"),
        pytest.param({"closes": [*BTC_CLOSES, BTC_CLOSES[-1]]}, id="a repeated date"),
        pytest.param(
            {"closes": [*BTC_CLOSES[:-1], {**BTC_CLOSES[-1], "close": "0"}]}, id="a zero close"
        ),
        pytest.param({"freq": "B"}, id="weekend dates for B"),
        pytest.param({"freq": "W"}, id="an unknown freq"),
        pytest.param({"horizonSteps": 0}, id="no steps"),
        pytest.param({"levels": [80, 95]}, id="an unknown field"),
    ],
)
def test_a_request_with_malformed_input_is_refused(changes: dict[str, Any]) -> None:
    response = client.post("/v1/forecasts", json={**BTC_REQUEST, **changes})

    assert response.status_code == 422


def test_too_little_history_is_refused_with_the_count_needed() -> None:
    response = client.post("/v1/forecasts", json={**BTC_REQUEST, "closes": BTC_CLOSES[-150:]})

    assert response.status_code == 422
    assert "at least 156" in response.json()["detail"]
