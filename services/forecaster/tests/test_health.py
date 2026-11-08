from fastapi.testclient import TestClient

from forecaster.app import app


def test_health_reports_healthy() -> None:
    client = TestClient(app)

    response = client.get("/health")

    assert response.status_code == 200
    assert response.json() == {"status": "healthy"}
