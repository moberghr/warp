#!/usr/bin/env bash
# Starts a FRESH PostgreSQL for one side of the performance comparison, replacing any previous one.
#
# The head and the base run each get their own container. They used to share one, head first, so the
# base run inherited tables, indexes and statistics the head run had already filled and vacuumed.
# Comparing a commit with itself then read ~50 buffers per job higher on "head" in most scenarios
# (fan-out 121 vs 182, concurrency 130 vs 184): a difference made by the run order, not the code.
set -euo pipefail

docker rm -f warpbench > /dev/null 2>&1 || true

docker run -d --name warpbench -p 5432:5432 \
  -e POSTGRES_PASSWORD=postgres \
  postgres@sha256:7e32e9833a6fb1c92c32552794cb6ed569d51b445a54907d35fc112ef39684db \
  -c shared_preload_libraries=pg_stat_statements \
  -c pg_stat_statements.track=top

# Not pg_isready. The image initialises on a TEMPORARY server, then stops it and starts the real one,
# and pg_isready answers yes for the temporary one: a CREATE EXTENSION sent then was cut off by the
# shutdown ("terminating connection due to administrator command") and failed the job before any
# benchmark ran. So retry the step that matters until it succeeds, and only accept a server that can
# then read the extension's view. The init-complete line marks the end of the temporary phase, so
# nothing below can land on it.
for i in $(seq 1 90); do
  if docker logs warpbench 2>&1 | grep -q "PostgreSQL init process complete" \
    && docker exec warpbench psql -U postgres -q -c "CREATE EXTENSION IF NOT EXISTS pg_stat_statements" > /dev/null 2>&1 \
    && docker exec warpbench psql -U postgres -tAc "SELECT count(*) FROM pg_stat_statements" > /dev/null 2>&1; then
    echo "postgres ready after ${i}s"
    exit 0
  fi
  sleep 1
done

echo "::error::PostgreSQL did not become ready"
docker logs warpbench
exit 1
