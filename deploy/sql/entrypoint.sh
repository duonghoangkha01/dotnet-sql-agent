#!/bin/bash
# SQL Server container entrypoint (replaces the image entrypoint, so this script is PID 1 and
# receives `docker stop`'s SIGTERM):
#   1. clear the ready flag, start sqlservr, wait until it accepts logins and every database is online
#   2. decide whether AdventureWorks2022 must be restored (only when a previous boot didn't finish)
#   3. apply every numbered script in deploy/sql (idempotent, runs on every boot)
#   4. mark the bootstrap ready (database marker + container flag file), then wait on sqlservr
# Any failure exits non-zero, so a half-initialized database is never reported healthy. A failing
# query aborts the boot instead of being read as "empty", so a transient error can never trigger
# a RESTORE ... WITH REPLACE over a good database.
set -euo pipefail

# The healthcheck requires this flag, so a restart is "not ready" from the first instant, even
# though the database marker from the previous boot still says Ready = 1.
READY_FLAG=/tmp/bootstrap-ready
rm -f "$READY_FLAG"

export SQLCMDPASSWORD="${MSSQL_SA_PASSWORD}"
# -I: QUOTED_IDENTIFIER ON, as Microsoft.Data.SqlClient connects. Objects created here must carry the
# same setting, or a later indexed view / computed column on them would fail under the API's connections.
SQLCMD=(/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -I -b -h -1 -W)
BOOTSTRAP_VERSION=1

/opt/mssql/bin/sqlservr &
SQL_PID=$!
# Forward the stop signal so SQL Server shuts down cleanly instead of being killed and crash-recovering.
trap 'kill -TERM "$SQL_PID" 2>/dev/null || true; wait "$SQL_PID" || true; exit 0' TERM INT

# scalar "<SQL>": prints the single result value. Aborts the boot if the query itself fails.
scalar() {
  local out
  out="$("${SQLCMD[@]}" -Q "SET NOCOUNT ON; $1" 2>&1)" || { echo "SQL query failed: $1" >&2; echo "$out" >&2; exit 1; }
  printf '%s' "$out" | tr -d '[:space:]'
}

# A dead sqlservr can never accept logins: stop at once instead of polling for minutes (weak SA
# password, missing EULA, out of memory all end here with the reason in the container log above).
sql_alive() { kill -0 "$SQL_PID" 2>/dev/null || { echo "sqlservr exited during startup; see its log above." >&2; exit 1; }; }

echo "Waiting for SQL Server..."
for _ in $(seq 1 90); do
  "${SQLCMD[@]}" -Q "SELECT 1" >/dev/null 2>&1 && break
  sql_alive
  sleep 2
done
"${SQLCMD[@]}" -Q "SELECT 1" >/dev/null   # fails hard (set -e) if it never came up

# Logins are accepted before the user databases are even started: for that stretch sys.databases reports
# them as ONLINE (state 0) and any query into them fails with error 904 ("cannot be autostarted"). So
# state alone cannot say "ready"; wait for the server's own "Recovery is complete" message first.
recovered=0
for _ in $(seq 1 90); do
  recovered="$(scalar "CREATE TABLE #l (d datetime, p nvarchar(100), t nvarchar(max)); INSERT #l EXEC sp_readerrorlog 0, 1, N'Recovery is complete'; SELECT COUNT(*) FROM #l")"
  [ "$recovered" != "0" ] && break
  sql_alive
  sleep 2
done
[ "$recovered" != "0" ] || { echo "SQL Server did not finish recovery in time." >&2; exit 1; }

# With recovery done, wait for any database still coming online. AdventureWorks2022 in RESTORING
# (state 1) is not waited for: a restore killed midway leaves it there for good, and the REPLACE
# restore below is what recovers it.
pending=""
for _ in $(seq 1 90); do
  pending="$(scalar "SELECT COUNT(*) FROM sys.databases WHERE state <> 0 AND NOT (name = N'AdventureWorks2022' AND state = 1)")"
  [ "$pending" = "0" ] && break
  sql_alive
  sleep 2
done
[ "$pending" = "0" ] || { echo "Databases did not come online in time." >&2; exit 1; }

# A previous boot finished only if both databases and the marker table exist and RestoreDone is set.
# A missing marker table (a boot killed between CREATE DATABASE and CREATE TABLE) counts as "not done".
need_restore=1
bootstrapped="$(scalar "SELECT CASE WHEN DB_ID('SqlAgent') IS NOT NULL AND DB_ID('AdventureWorks2022') IS NOT NULL AND OBJECT_ID('SqlAgent.dbo.BootstrapState') IS NOT NULL THEN 1 ELSE 0 END")"
if [ "$bootstrapped" = "1" ]; then
  "${SQLCMD[@]}" -Q "UPDATE SqlAgent.dbo.BootstrapState SET Ready = 0, UpdatedUtc = SYSUTCDATETIME()"
  restore_done="$(scalar "SELECT COUNT(*) FROM SqlAgent.dbo.BootstrapState WHERE RestoreDone = 1")"
  [[ "$restore_done" =~ ^[0-9]+$ ]] || { echo "Unexpected marker value: '$restore_done'" >&2; exit 1; }
  [ "$restore_done" -ge 1 ] && need_restore=0
fi
# A database stuck in RESTORING is unusable whatever the marker says.
stuck="$(scalar "SELECT COUNT(*) FROM sys.databases WHERE name = N'AdventureWorks2022' AND state = 1")"
if [ "$stuck" != "0" ]; then need_restore=1; fi

if [ "$need_restore" = 1 ]; then
  echo "Restoring AdventureWorks2022 (REPLACE also recovers a restore that was killed midway)..."
  "${SQLCMD[@]}" -Q "RESTORE DATABASE AdventureWorks2022 FROM DISK = N'/bak/AdventureWorks2022.bak' WITH MOVE N'AdventureWorks2022' TO N'/var/opt/mssql/data/AdventureWorks2022.mdf', MOVE N'AdventureWorks2022_log' TO N'/var/opt/mssql/data/AdventureWorks2022_log.ldf', REPLACE, STATS = 20"
else
  echo "AdventureWorks2022 already restored; skipping."
fi

/bin/bash /scripts/apply-scripts.sh

"${SQLCMD[@]}" -Q "UPDATE SqlAgent.dbo.BootstrapState SET RestoreDone = 1, Ready = 1, UpdatedUtc = SYSUTCDATETIME() WHERE Version = ${BOOTSTRAP_VERSION}"
touch "$READY_FLAG"
echo "Bootstrap complete (version ${BOOTSTRAP_VERSION})."

wait "$SQL_PID"
