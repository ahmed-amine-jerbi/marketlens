# Builds and runs the forecaster. Context: services/forecaster. Only the API calls it, on the
# Compose network; it publishes no port (ADR 0005).
FROM python:3.12.13-slim
COPY --from=ghcr.io/astral-sh/uv:0.13.6 /uv /bin/uv
WORKDIR /app

ENV UV_COMPILE_BYTECODE=1 UV_LINK_MODE=copy UV_NO_DEV=1

# Dependencies first, so their layer is cached until the lockfile changes.
COPY pyproject.toml uv.lock .python-version ./
RUN uv sync --locked --no-install-project

COPY forecaster/ forecaster/

RUN useradd --system --no-create-home forecaster
USER forecaster
EXPOSE 8001
CMD ["/app/.venv/bin/uvicorn", "forecaster.app:app", "--host", "0.0.0.0", "--port", "8001"]
