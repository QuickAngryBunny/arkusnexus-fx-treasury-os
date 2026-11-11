#!/usr/bin/env bash
# Start the local stack: Postgres + Redis (Docker), the .NET API and the Next.js web app.
# Usage: ./scripts/dev.sh        Stop everything with Ctrl+C (containers keep running; `docker compose stop` stops them).
set -euo pipefail
cd "$(dirname "$0")/.."

if ! docker info >/dev/null 2>&1; then
  echo "Starting Docker Desktop…"
  open -a Docker 2>/dev/null || true
  until docker info >/dev/null 2>&1; do sleep 2; done
fi

docker compose up -d
echo "Waiting for Postgres…"
until [ "$(docker inspect -f '{{.State.Health.Status}}' "$(docker compose ps -q postgres)")" = "healthy" ]; do sleep 1; done

[ -f apps/web/.env.local ] || cp apps/web/.env.example apps/web/.env.local
[ -d node_modules ] || pnpm install

mkdir -p .logs
echo "Starting API on http://localhost:5080 (log: .logs/api.log)"
dotnet run --project src/FxOs.Api > .logs/api.log 2>&1 &
API_PID=$!
SCHED_PID=""
trap 'echo; echo "Stopping API and rate scheduler…"; kill $API_PID $SCHED_PID 2>/dev/null || true' EXIT INT TERM

until curl -sf http://localhost:5080/health >/dev/null; do
  if ! kill -0 $API_PID 2>/dev/null; then echo "API failed to start — see .logs/api.log"; exit 1; fi
  sleep 1
done

# Daily Banxico sync (and forecast refresh) when the worker is installed and a token is configured.
if [ -x workers/python/.venv/bin/fxworkers ] && grep -q '^BANXICO_TOKEN=.' workers/python/.env 2>/dev/null; then
  echo "Starting rate scheduler (log: .logs/rates.log)"
  workers/python/.venv/bin/fxworkers rates schedule > .logs/rates.log 2>&1 &
  SCHED_PID=$!
else
  echo "Rate scheduler not started: install workers/python (.venv) and set BANXICO_TOKEN in workers/python/.env"
fi

echo "Starting web on http://localhost:3000"
pnpm dev:web
