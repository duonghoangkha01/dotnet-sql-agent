-- Logins and database users. Idempotent: safe to re-run on every boot.
--
-- One DB user per agent role. They deliberately do NOT get db_datareader: each user can read only
-- what deploy/sql/40-role-grants.generated.sql grants, so the database enforces the same allowlist
-- as the guardrail. A bug in the guardrail alone therefore cannot expose another role's data.
-- sqlagent_app gets metadata visibility only (no SELECT), for schema introspection.
--
-- Passwords arrive as sqlcmd variables from the environment; nothing secret is stored in this file.

USE master;
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'sqlagent_sales_rep')
    CREATE LOGIN sqlagent_sales_rep WITH PASSWORD = N'$(SQL_SALES_REP_PASSWORD)', DEFAULT_DATABASE = AdventureWorks2022;
ELSE
    ALTER LOGIN sqlagent_sales_rep WITH PASSWORD = N'$(SQL_SALES_REP_PASSWORD)';

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'sqlagent_finance')
    CREATE LOGIN sqlagent_finance WITH PASSWORD = N'$(SQL_FINANCE_PASSWORD)', DEFAULT_DATABASE = AdventureWorks2022;
ELSE
    ALTER LOGIN sqlagent_finance WITH PASSWORD = N'$(SQL_FINANCE_PASSWORD)';

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'sqlagent_admin')
    CREATE LOGIN sqlagent_admin WITH PASSWORD = N'$(SQL_ADMIN_PASSWORD)', DEFAULT_DATABASE = AdventureWorks2022;
ELSE
    ALTER LOGIN sqlagent_admin WITH PASSWORD = N'$(SQL_ADMIN_PASSWORD)';

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'sqlagent_app')
    CREATE LOGIN sqlagent_app WITH PASSWORD = N'$(SQL_APP_PASSWORD)';
ELSE
    ALTER LOGIN sqlagent_app WITH PASSWORD = N'$(SQL_APP_PASSWORD)';
GO

USE AdventureWorks2022;
GO

-- A RESTORE ... WITH REPLACE brings back the backup's user list, so users are re-created or
-- re-mapped to their logins here on every boot.
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'sqlagent_sales_rep')
    CREATE USER sqlagent_sales_rep FOR LOGIN sqlagent_sales_rep;
ELSE
    ALTER USER sqlagent_sales_rep WITH LOGIN = sqlagent_sales_rep;

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'sqlagent_finance')
    CREATE USER sqlagent_finance FOR LOGIN sqlagent_finance;
ELSE
    ALTER USER sqlagent_finance WITH LOGIN = sqlagent_finance;

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'sqlagent_admin')
    CREATE USER sqlagent_admin FOR LOGIN sqlagent_admin;
ELSE
    ALTER USER sqlagent_admin WITH LOGIN = sqlagent_admin;

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'sqlagent_app')
    CREATE USER sqlagent_app FOR LOGIN sqlagent_app;
ELSE
    ALTER USER sqlagent_app WITH LOGIN = sqlagent_app;

-- sqlagent_app's metadata-only VIEW DEFINITION on this database is granted in 40-role-grants.generated.sql,
-- right after that script resets everything the users hold, so drift on sqlagent_app is repaired too.
-- (No DENY VIEW DEFINITION anywhere: it would break introspection.)
GO
