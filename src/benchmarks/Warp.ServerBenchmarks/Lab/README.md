# Running the labs without the host's port-forward in the way

Reaching a Testcontainers Postgres from Windows goes through Docker Desktop's port-forward proxy,
and that proxy costs **11-15x in statement throughput**: the same `pgbench` binary running the same
query gets 15,237 tps over the container's own loopback and 1,013 tps through the proxy, about
**0.92 ms added to every round trip**.

That is enough to dominate a measurement. Run the lab itself in a container on the same network as
Postgres and nothing sits in between.

```sh
docker network create warplab
docker run -d --network warplab --name warppg -e POSTGRES_PASSWORD=postgres postgres:latest \
  -c shared_preload_libraries=pg_stat_statements -c pg_stat_statements.track=top

dotnet publish src/benchmarks/Warp.ServerBenchmarks/Warp.ServerBenchmarks.csproj \
  -c Release -r linux-x64 --self-contained false -o artifacts/lab-linux

# Fresh database per arm, so arms stay comparable.
docker exec warppg psql -U postgres -q -c "DROP DATABASE IF EXISTS warplab" -c "CREATE DATABASE warplab"

docker run --rm --network warplab -v "<abs repo path>/artifacts/lab-linux:/app" \
  mcr.microsoft.com/dotnet/sdk:10.0 /app/Warp.ServerBenchmarks \
  cluster --jobs=10000 --workers=16 --servers=1 --repeats=3 \
  --connection="Host=warppg;Port=5432;Username=postgres;Password=postgres;Database=warplab"
```

Run the **published apphost**, not `dotnet Warp.ServerBenchmarks.dll` — `ClusterLab` spawns its child
servers from `Environment.ProcessPath`, which is the dotnet host under the latter. It compensates, but
the apphost is the shape it was written for. From Git Bash, `export MSYS_NO_PATHCONV=1` or the
container-side paths get rewritten to Windows ones.

## What moving in-container did and did not change

Throughput rose 15-26% at every point. It did **not** change the scaling shape: the implied serial
fraction went from 0.33 to 0.29, and per-worker returns fall off exactly as before. Warp is **not**
round-trip bound — a 3.7x faster wire bought 15-26%, not 3.7x.

The one shape change is that distribution now costs something visible, where the proxy had masked it:
at four total workers, 1x4 = 267, 2x2 = 247, 4x1 = 236 jobs/sec (in-container), against a flat
217 / 218 / 212 through the proxy.

## Still unexplained

At 16 workers Postgres burns ~2.5 cores while `pg_stat_statements` attributes only ~0.18 cores to
statement execution. Planning is **not** the gap — measured with `track_planning=on`, planning is 4.3%
of execution time, so Npgsql auto-prepare is not the lever it looked like. Connection and session
churn, WAL, and the background workers are what is left to account for it.
