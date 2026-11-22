#!/usr/bin/env bash
# Starts a FRESH SQL Server for one side of the performance comparison, replacing any previous one.
#
# Same reason as start-bench-postgres.sh: the head and the base run each get their own server. Only
# PostgreSQL did at first, and the base run reused the SQL Server the head run had just used. Comparing
# a commit with itself then read the idle server at 4.5 statements and about 42 pages per second on
# "head" against 4.06 and 13.9 on "base", the same way in three runs out of three: a difference made by
# which side met the server first, not by the code.
set -euo pipefail

docker rm -f warpbench-mssql > /dev/null 2>&1 || true

docker run -d --name warpbench-mssql -p 1433:1433 \
  -e ACCEPT_EULA=Y -e 'MSSQL_SA_PASSWORD=Warp!Bench1' \
  mcr.microsoft.com/mssql/server:2022-latest

for i in $(seq 1 90); do
  if docker exec warpbench-mssql /opt/mssql-tools18/bin/sqlcmd \
       -S localhost -U sa -P 'Warp!Bench1' -C -Q "SELECT 1" > /dev/null 2>&1; then
    echo "sql server ready after ${i}s"
    exit 0
  fi
  sleep 1
done

echo "::error::SQL Server did not become ready"
docker logs warpbench-mssql
exit 1
