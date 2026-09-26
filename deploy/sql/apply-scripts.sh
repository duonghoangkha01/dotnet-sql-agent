#!/bin/bash
# Runs every numbered *.sql script in order against the local SQL Server.
# Used by entrypoint.sh on every boot, and by hand to re-apply after editing a script:
#   docker compose exec sqlserver /bin/bash /scripts/apply-scripts.sh
# Every script is idempotent, so running them repeatedly is safe.
set -euo pipefail

export SQLCMDPASSWORD="${MSSQL_SA_PASSWORD}"

# -I: QUOTED_IDENTIFIER ON, matching Microsoft.Data.SqlClient (see entrypoint.sh).
for script in /scripts/[0-9][0-9]-*.sql; do
  echo "== applying ${script##*/}"
  /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -I -b -i "$script" \
    -v SQL_SALES_REP_PASSWORD="$SQL_SALES_REP_PASSWORD" \
       SQL_FINANCE_PASSWORD="$SQL_FINANCE_PASSWORD" \
       SQL_ADMIN_PASSWORD="$SQL_ADMIN_PASSWORD" \
       SQL_APP_PASSWORD="$SQL_APP_PASSWORD"
done
