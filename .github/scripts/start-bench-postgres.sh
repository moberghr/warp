#!/usr/bin/env bash
# Starts a FRESH PostgreSQL for one side of the performance comparison, replacing any previous one.
#
# The head and the base run each get their own container. They used to share one, head first, so the
# base run inherited tables, indexes and statistics the head run had already filled and vacuumed.
# Comparing a commit with itself then read ~50 buffers per job higher on "head" in most scenarios
# (fan-out 121 vs 182, concurrency 130 vs 184): a difference made by the run order, not the code.
#
# track_planning is on so the "DB ms" column is execution + planning time, as it is labelled.
set -euo pipefail

# The image is the version under test: BENCH_POSTGRES_IMAGE (or the first argument) names it, pinned to an
# exact tag by the workflow's database matrix. The default is the digest every run used before that matrix.
IMAGE="${1:-${BENCH_POSTGRES_IMAGE:-postgres@sha256:7e32e9833a6fb1c92c32552794cb6ed569d51b445a54907d35fc112ef39684db}}"
echo "starting $IMAGE"

docker rm -f warpbench > /dev/null 2>&1 || true

# full_page_writes=off: the first change to a page after a checkpoint writes the whole page into the WAL, and
# how many such pages a run touches depends on where PostgreSQL's checkpoints fall, not on Warp. Runs of
# identical work read up to 16% apart in WAL/job (WAL FPI/job told them apart exactly) and failed the WAL
# gate. Off, WAL/job counts only the changes Warp makes. Fine for a throwaway benchmark container; never in
# production, where it risks torn pages after a crash.

# Capped at 2 CPUs and 4 GB (BENCH_DB_CPUS / BENCH_DB_MEMORY to change): the database then has the same
# room on every runner whatever else the machine is doing, and the benchmark process keeps the other cores,
# so the two do not compete for the same ones. Without it the database took whatever the 4-core runner had
# left at that moment.
docker run -d --name warpbench -p 5432:5432 \
  --cpus "${BENCH_DB_CPUS:-2}" --memory "${BENCH_DB_MEMORY:-4g}" \
  -e POSTGRES_PASSWORD=postgres \
  "$IMAGE" \
  -c shared_preload_libraries=pg_stat_statements \
  -c pg_stat_statements.track=top \
  -c pg_stat_statements.track_planning=on \
  -c full_page_writes=off

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
