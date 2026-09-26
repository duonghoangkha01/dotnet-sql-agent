#!/usr/bin/env bash
# Checks the running local stack against the infrastructure acceptance criteria:
# row-level security per role, write/DDL denial, denied columns and tables, MAXDOP, network exposure.
# Expected row counts are computed as dbo (which the policy exempts), so nothing is hardcoded.
#
#   docker compose up -d --wait sqlserver aspire-dashboard
#   scripts/verify-infra.sh                    # add --restart-check to also test a second boot
set -uo pipefail

cd "$(dirname "$0")/.."
# Git Bash on Windows rewrites paths like /bin/bash inside `docker exec` arguments. Turn that off.
export MSYS_NO_PATHCONV=1

pass=0
fail=0
ok()  { pass=$((pass + 1)); echo "  PASS  $1"; }
bad() { fail=$((fail + 1)); echo "  FAIL  $1"; [ -n "${2:-}" ] && echo "        $2"; }

# sql_as <db user> <env var holding its password>: runs SQL from stdin inside the container.
# -I turns QUOTED_IDENTIFIER on, as Microsoft.Data.SqlClient does. Without it sqlcmd's default (OFF)
# makes writes to tables with indexed views fail with error 1934 before the permission check runs,
# which would let a broken permission model look "denied".
sql_as() {
  docker compose exec -T sqlserver bash -c \
    "SQLCMDPASSWORD=\"\$$2\" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U $1 -I -b -h -1 -W -i /dev/stdin" 2>&1
}
sa()        { sql_as sa MSSQL_SA_PASSWORD; }
sales_rep() { sql_as sqlagent_sales_rep SQL_SALES_REP_PASSWORD; }
finance()   { sql_as sqlagent_finance SQL_FINANCE_PASSWORD; }
admin()     { sql_as sqlagent_admin SQL_ADMIN_PASSWORD; }
app()       { sql_as sqlagent_app SQL_APP_PASSWORD; }

# value <runner> <SQL...>: the first result row as a bare token.
value() { local runner="$1"; shift; "$runner" <<<"SET NOCOUNT ON; USE AdventureWorks2022; $*" | grep -v "^Changed database" | head -1 | tr -d '[:space:]'; }

# Both sides must be numbers: two identical error messages (a stopped container, a failed login) are
# equal strings and must not count as a match.
expect_eq() { # name expected actual
  if [[ "$2" =~ ^[0-9]+$ && "$3" =~ ^[0-9]+$ && "$2" = "$3" ]]; then ok "$1 ($3)"; else bad "$1" "expected '$2', got '$(head -c 200 <<<"$3")'"; fi
}

# expect_denied <name> <runner> <SQL> <regex for the error text>
expect_denied() {
  local out
  out="$($2 <<<"SET NOCOUNT ON; $3")"
  if [ $? -ne 0 ] && grep -qiE "$4" <<<"$out"; then ok "$1"; else bad "$1" "expected an error matching /$4/, got: $(head -c 200 <<<"$out")"; fi
}

echo "== Bootstrap"
ready="$(value sa "SELECT CAST(Ready AS int) FROM SqlAgent.dbo.BootstrapState WHERE Version = 1")"
expect_eq "bootstrap marker is Ready" 1 "$ready"
expect_eq "MAXDOP is 1 (SESSION_CONTEXT parallel-plan workaround)" 1 \
  "$(value sa "SELECT CAST(value AS int) FROM sys.database_scoped_configurations WHERE name = N'MAXDOP'")"

echo "== Row-level security: sales rep, territory 1"
declare -A expected=(
  [Sales.SalesOrderHeader]="SELECT COUNT(*) FROM Sales.SalesOrderHeader WHERE TerritoryID = 1"
  [Sales.Customer]="SELECT COUNT(*) FROM Sales.Customer WHERE TerritoryID = 1"
  [Sales.SalesOrderDetail]="SELECT COUNT(*) FROM Sales.SalesOrderDetail d JOIN Sales.SalesOrderHeader h ON h.SalesOrderID = d.SalesOrderID WHERE h.TerritoryID = 1"
  [Sales.SalesOrderHeaderSalesReason]="SELECT COUNT(*) FROM Sales.SalesOrderHeaderSalesReason r JOIN Sales.SalesOrderHeader h ON h.SalesOrderID = r.SalesOrderID WHERE h.TerritoryID = 1"
  [Sales.Store]="SELECT COUNT(*) FROM Sales.Store s JOIN Sales.SalesPerson sp ON sp.BusinessEntityID = s.SalesPersonID WHERE sp.TerritoryID = 1"
)
for table in "${!expected[@]}"; do
  want="$(value sa "${expected[$table]}")"
  all="$(value sa "SELECT COUNT(*) FROM $table")"
  # A territory-1 expectation of 0 or "everything" would make the comparison below prove nothing.
  if [[ "$want" =~ ^[0-9]+$ && "$all" =~ ^[0-9]+$ ]] && [ "$want" -gt 0 ] && [ "$want" -lt "$all" ]; then
    ok "$table: territory 1 is a strict, non-empty subset ($want of $all)"
  else
    bad "$table: territory 1 expectation is degenerate" "territory 1 = '$want', all rows = '$all'"
  fi
  got="$(sales_rep <<<"SET NOCOUNT ON; EXEC sp_set_session_context N'territory_id', 1, @read_only = 1; SELECT COUNT(*) FROM $table;" | tail -1 | tr -d '[:space:]')"
  expect_eq "$table returns only territory 1 rows" "$want" "$got"
  none="$(sales_rep <<<"SET NOCOUNT ON; SELECT COUNT(*) FROM $table;" | tail -1 | tr -d '[:space:]')"
  expect_eq "$table returns 0 rows without a territory in the session" 0 "$none"
  # Each predicate function carries its own copy of the exemption list, so every table is checked.
  expect_eq "finance sees all of $table" "$all" "$(value finance "SELECT COUNT(*) FROM $table")"
  expect_eq "admin sees all of $table" "$all" "$(value admin "SELECT COUNT(*) FROM $table")"
done

echo "== Row-level security: other roles and territories"
total="$(value sa "SELECT COUNT(*) FROM Sales.SalesOrderHeader")"
expect_eq "finance sees every order with no session context" "$total" "$(value finance "SELECT COUNT(*) FROM Sales.SalesOrderHeader")"
expect_eq "admin sees every order with no session context" "$total" "$(value admin "SELECT COUNT(*) FROM Sales.SalesOrderHeader")"
t4_want="$(value sa "SELECT COUNT(*) FROM Sales.SalesOrderHeader WHERE TerritoryID = 4")"
t4_got="$(sales_rep <<<"SET NOCOUNT ON; EXEC sp_set_session_context N'territory_id', 4, @read_only = 1; SELECT COUNT(*) FROM Sales.SalesOrderHeader;" | tail -1 | tr -d '[:space:]')"
expect_eq "sales rep in territory 4 sees exactly territory 4" "$t4_want" "$t4_got"
expect_denied "session context is read-only once set (no self-service role change)" sales_rep \
  "EXEC sp_set_session_context N'territory_id', 1, @read_only = 1; EXEC sp_set_session_context N'territory_id', 4;" "read.only"

echo "== Least privilege: writes and DDL denied for every reader"
for role in sales_rep finance admin; do
  # Inside a transaction that is always rolled back: if a permission regression ever let one of these
  # succeed, the check fails but the data survives (finance/admin bypass RLS, and order deletes cascade).
  expect_denied "$role cannot INSERT" "$role" "BEGIN TRAN; INSERT Sales.SalesOrderHeader (TerritoryID) VALUES (1); ROLLBACK;" "permission|denied"
  expect_denied "$role cannot UPDATE" "$role" "BEGIN TRAN; UPDATE Sales.SalesOrderHeader SET Comment = N'x'; ROLLBACK;" "permission|denied"
  expect_denied "$role cannot DELETE" "$role" "BEGIN TRAN; DELETE Sales.SalesOrderHeader; ROLLBACK;" "permission|denied"
  expect_denied "$role cannot read the SqlAgent database (audit log)" "$role" \
    "SELECT COUNT(*) FROM SqlAgent.dbo.AuditLog;" "not able to access|permission was denied"
  # guest reaches tempdb: a permanent table there would outlive the session and could carry rows across territories.
  expect_denied "$role cannot create a permanent table in tempdb" "$role" "CREATE TABLE tempdb.dbo.verify_probe (a int);" "CREATE TABLE permission"
  expect_denied "$role cannot impersonate a database user" "$role" "EXECUTE AS USER = N'dbo';" "cannot execute as"
  expect_denied "$role cannot impersonate a login" "$role" "EXECUTE AS LOGIN = N'sa';" "cannot execute as"
  expect_denied "$role cannot run xp_cmdshell" "$role" "EXEC xp_cmdshell 'whoami';" "permission|denied"
  # Selects a column every role may read, so the failure can only be the missing CREATE TABLE permission.
  expect_denied "$role cannot SELECT ... INTO a permanent table" "$role" "SELECT TOP 1 TerritoryID INTO dbo.NewTable FROM Sales.SalesTerritory;" "CREATE TABLE permission"
done

echo "== Restricted columns and tables (sales rep)"
expect_denied "cannot read Sales.SalesTerritory.SalesLastYear (per-territory revenue)" sales_rep \
  "SELECT SalesLastYear FROM Sales.SalesTerritory;" "permission|denied"
expect_denied "SELECT * over SalesTerritory is refused" sales_rep "SELECT * FROM Sales.SalesTerritory;" "permission|denied"
for object in Sales.SalesTerritoryHistory Sales.SalesPersonQuotaHistory \
              Purchasing.PurchaseOrderHeader Purchasing.PurchaseOrderDetail Sales.vSalesPerson \
              HumanResources.Employee HumanResources.EmployeePayHistory Person.Password; do
  expect_denied "cannot read $object" sales_rep "SELECT COUNT(*) FROM $object;" "permission|denied"
done
# SalesPerson is readable for its non-monetary columns only. It has no row-level policy: which salesperson
# sits in which territory is not sensitive, the money columns are.
for column in SalesYTD SalesLastYear Bonus SalesQuota CommissionPct; do
  expect_denied "cannot read Sales.SalesPerson.$column" sales_rep "SELECT $column FROM Sales.SalesPerson;" "permission|denied"
done
expect_denied "SELECT * over SalesPerson is refused" sales_rep "SELECT * FROM Sales.SalesPerson;" "permission|denied"
expect_eq "can read the non-monetary SalesPerson columns" "$(value sa "SELECT COUNT(*) FROM Sales.SalesPerson")" \
  "$(value sales_rep "SELECT COUNT(BusinessEntityID) FROM Sales.SalesPerson")"
expect_denied "admin cannot read Sales.CreditCard.CardNumber" admin "SELECT CardNumber FROM Sales.CreditCard;" "permission|denied"
expect_denied "admin cannot read HumanResources.Employee.NationalIDNumber" admin "SELECT NationalIDNumber FROM HumanResources.Employee;" "permission|denied"
expect_denied "finance cannot read HumanResources.EmployeePayHistory" finance "SELECT COUNT(*) FROM HumanResources.EmployeePayHistory;" "permission|denied"
expect_eq "can read the identifying SalesTerritory columns" 10 \
  "$(value sales_rep "SELECT COUNT(*) FROM (SELECT TerritoryID, [Name] FROM Sales.SalesTerritory) AS t")"
expect_eq "finance can read SalesLastYear" 10 "$(value finance "SELECT COUNT(SalesLastYear) FROM Sales.SalesTerritory")"

echo "== Application user (sqlagent_app): append-only audit log, metadata but no data"
audit_marker="verify-infra-$RANDOM$RANDOM"
expect_eq "app can INSERT and read its own audit row (rolled back)" 1 \
  "$(value app "USE SqlAgent; BEGIN TRAN; INSERT dbo.AuditLog (Sub, [Role], [Sql], Allowed) VALUES (N'$audit_marker', N'verify', N'SELECT 1', 1); SELECT COUNT(*) FROM dbo.AuditLog WHERE Sub = N'$audit_marker'; ROLLBACK;")"
expect_denied "app cannot UPDATE the audit log" app "USE SqlAgent; BEGIN TRAN; UPDATE dbo.AuditLog SET Question = N'x'; ROLLBACK;" "permission|denied"
expect_denied "app cannot DELETE from the audit log" app "USE SqlAgent; BEGIN TRAN; DELETE dbo.AuditLog; ROLLBACK;" "permission|denied"
expect_denied "app cannot read business data" app "USE AdventureWorks2022; SELECT COUNT(*) FROM Sales.SalesOrderHeader;" "permission|denied"
cols_want="$(value sa "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'Sales.SalesOrderHeader')")"
expect_eq "app can read schema metadata (VIEW DEFINITION)" "$cols_want" \
  "$(value app "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'Sales.SalesOrderHeader')")"

echo "== Network exposure"
exposed=""
published=0
for id in $(docker compose ps -q); do
  while read -r line; do
    [ -z "$line" ] && continue
    published=$((published + 1))
    case "$line" in *" -> 127.0.0.1:"*) ;; *) exposed="$exposed $line";; esac
  done < <(docker port "$id" 2>/dev/null)
done
# "Nothing exposed" must not pass just because nothing was inspected: the dashboard always publishes 18888.
if [ "$published" -eq 0 ]; then
  bad "no published ports were found to check (expected at least the dashboard on 127.0.0.1:18888)"
elif [ -z "$exposed" ]; then
  ok "all $published published port mappings are bound to 127.0.0.1"
else
  bad "ports published beyond 127.0.0.1" "$exposed"
fi
# probe <host> <port>: succeeds only if a TCP connection opens. A missing `timeout` or /dev/tcp makes every
# probe fail, which would read as "not reachable" everywhere, so the loopback probe below doubles as the
# positive control for the very same command.
probe() { timeout 2 bash -c "exec 3<>/dev/tcp/$1/$2" 2>/dev/null; }
if probe 127.0.0.1 18888; then ok "dashboard reachable on 127.0.0.1:18888 (probe command works)"; else bad "dashboard not reachable on 127.0.0.1:18888 (or the probe command, timeout + /dev/tcp, is unavailable)"; fi

# Probe every local IPv4 address (a machine can have several adapters, e.g. VPN or VM switches).
lan_ips="$(ipconfig 2>/dev/null | tr -d '\r' | awk '/IPv4/ {print $NF}' | grep -vE '^(127\.|169\.254\.)' || true)"
[ -z "$lan_ips" ] && lan_ips="$(hostname -I 2>/dev/null | tr ' ' '\n' | grep -vE '^(127\.|169\.254\.|$)' || true)"
if [ -z "$lan_ips" ]; then
  echo "  SKIP  no non-loopback IPv4 address found to probe"
elif ! command -v timeout >/dev/null; then
  echo "  SKIP  no timeout command: every LAN probe would fail and prove nothing"
else
  reachable=""
  probes=0
  for ip in $lan_ips; do
    # Only 18888 is published (on 127.0.0.1); the others are closed by design, so 18888 is the probe that can bite.
    for port in 1433 11434 18888 18889 18890; do
      probes=$((probes + 1))
      if probe "$ip" "$port"; then reachable="$reachable $ip:$port"; fi
    done
  done
  [ -z "$reachable" ] && ok "none of $probes probes reached SQL Server, Ollama or the dashboard via a LAN address" || bad "reachable via a LAN address" "$reachable"
fi

if [ "${1:-}" = "--restart-check" ]; then
  echo "== Second boot (idempotent scripts, no re-restore, never ready mid-way)"
  # Permission drift added by hand (a role membership, a schema grant) must be gone after the next boot:
  # the grants script resets everything the reader users hold before granting the intended set.
  sa <<<"USE AdventureWorks2022; ALTER ROLE db_datareader ADD MEMBER sqlagent_sales_rep; ALTER ROLE db_datareader ADD MEMBER sqlagent_app; GRANT SELECT ON SCHEMA::HumanResources TO sqlagent_sales_rep;" >/dev/null
  # The container log keeps every earlier boot, and the host and the Docker VM clocks can disagree, so the
  # new boot's lines are found by position: everything after the lines that existed before the restart.
  log_lines_before="$(docker compose logs --no-log-prefix sqlserver 2>&1 | wc -l)"
  docker compose restart sqlserver >/dev/null 2>&1
  # The ready flag must be gone from the first instant of the new boot, even though the database
  # marker from the previous boot still says Ready = 1. "absent" must be printed: a failed exec
  # (container not running) prints nothing and is not evidence of anything.
  flag_state="$(docker compose exec -T sqlserver sh -c 'if test -f /tmp/bootstrap-ready; then echo present; else echo absent; fi' 2>/dev/null | tr -d '[:space:]')"
  case "$flag_state" in
    absent)  ok "ready flag is cleared at the start of the new boot" ;;
    present) bad "ready flag was still present right after the restart" ;;
    *)       bad "could not inspect the ready flag right after the restart" "exec printed: '$flag_state'" ;;
  esac
  if docker compose up -d --wait sqlserver >/dev/null 2>&1; then ok "sqlserver healthy again after a restart"; else bad "sqlserver did not become healthy after a restart"; fi
  boot_log="$(docker compose logs --no-log-prefix sqlserver 2>&1 | tail -n +$((log_lines_before + 1)))"
  grep -q "already restored" <<<"$boot_log" && ok "the new boot skipped the restore" || bad "the new boot did not report skipping the restore"
  grep -q "Bootstrap complete" <<<"$boot_log" && ok "the new boot re-applied the scripts and completed" || bad "the new boot did not complete the bootstrap"
  # The stop that ended the previous boot is logged after log_lines_before was taken, so it is in boot_log.
  grep -qiE "SQL Server is terminating|Service Broker manager has shut down" <<<"$boot_log" \
    && ok "SQL Server logged a clean shutdown on stop" || bad "no clean-shutdown message for this stop (stop signal not reaching SQL Server?)"
  expect_eq "boot removed role memberships added by hand (reader and app)" 0 \
    "$(value sa "SELECT COUNT(*) FROM sys.database_role_members WHERE member_principal_id IN (USER_ID(N'sqlagent_sales_rep'), USER_ID(N'sqlagent_app'))")"
  expect_denied "boot removed the app's data access added by hand" app "USE AdventureWorks2022; SELECT COUNT(*) FROM Sales.SalesOrderHeader;" "permission|denied"
  expect_eq "boot kept the app's metadata access after the reset" "$cols_want" \
    "$(value app "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'Sales.SalesOrderHeader')")"
  expect_denied "boot removed a schema grant added by hand" sales_rep "SELECT COUNT(*) FROM HumanResources.EmployeePayHistory;" "permission|denied"
fi

echo
echo "Result: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
