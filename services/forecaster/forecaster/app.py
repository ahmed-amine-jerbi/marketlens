"""HTTP entry point of the forecaster. Reachable only on the internal Docker network."""

from fastapi import FastAPI

app = FastAPI(title="Marketlens forecaster", version="0.1.0")


@app.get("/health")
def read_health() -> dict[str, str]:
    """Liveness probe for container health checks."""
    return {"status": "healthy"}
