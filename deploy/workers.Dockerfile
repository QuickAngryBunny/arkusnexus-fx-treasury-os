# Python workers (rates, curves, news, forecasts, tenant jobs) for Cloud Run jobs or a Cloud Run service.
# Build from the repository root: docker build -f deploy/workers.Dockerfile -t fxos-workers .
# Run one command per job, e.g. `fxworkers rates sync`, or `fxworkers rates schedule` as a long-running worker.
# Settings: FX_CONTROL_PLANE_DSN, FX_RAW_STORE_URL=gs://bucket/prefix, BANXICO_TOKEN and GNEWS_API_KEY (from Secret Manager).

FROM python:3.13-slim
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
WORKDIR /app
COPY workers/python/pyproject.toml ./
COPY workers/python/src/ src/
RUN pip install --no-cache-dir ".[gcp]" \
 && useradd --create-home worker
USER worker
ENTRYPOINT ["fxworkers"]
# Default: the rate scheduler (Banxico sync, curves, forecasts, news, "Sync now" requests, tenant setup jobs).
CMD ["rates", "schedule"]
