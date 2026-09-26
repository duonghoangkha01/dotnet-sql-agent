-- PLACEHOLDER until the schema catalog phase.
-- That phase replaces this file with grants generated from semantic.yaml
-- (`SqlAgent.Cli emit-grants`), so the database enforces the same allowlist as the guardrail.
-- Until then this hand-written minimum covers exactly the tables the row-level-security checks use.
--
-- Convergent, not add-only: it first strips everything the three reader users and sqlagent_app hold beyond public
-- (role memberships, and GRANT/DENY on the database, schemas, objects, columns and other users),
-- then grants the intended set. A grant removed from this file, or added by hand, therefore
-- disappears from an existing volume on the next boot. CONNECT is kept: without it nobody can log in.
-- The generated replacement must keep this reset preamble.
-- Runs as one transaction so a reader is never left without its grants after a partial failure.

USE AdventureWorks2022;
GO
SET XACT_ABORT ON;
GO
BEGIN TRANSACTION;
GO

DECLARE @reset nvarchar(max) = N'';

-- Role memberships (db_datareader, db_owner, ...): the readers hold none.
SELECT @reset += N'ALTER ROLE ' + QUOTENAME(r.name) + N' DROP MEMBER ' + QUOTENAME(u.name) + N';' + NCHAR(10)
FROM sys.database_role_members AS m
JOIN sys.database_principals   AS r ON r.principal_id = m.role_principal_id
JOIN sys.database_principals   AS u ON u.principal_id = m.member_principal_id
WHERE u.name IN (N'sqlagent_sales_rep', N'sqlagent_finance', N'sqlagent_admin', N'sqlagent_app');

-- Permissions: class 0 = database, 1 = object or column, 3 = schema, 4 = user/role (IMPERSONATE, ...).
-- CASCADE makes the REVOKE work for permissions that were granted WITH GRANT OPTION as well.
SELECT @reset += N'REVOKE ' + p.permission_name
               + CASE p.class
                   WHEN 1 THEN N' ON ' + QUOTENAME(OBJECT_SCHEMA_NAME(p.major_id)) + N'.' + QUOTENAME(OBJECT_NAME(p.major_id))
                             + CASE WHEN p.minor_id > 0 THEN N' (' + QUOTENAME(c.name) + N')' ELSE N'' END
                   WHEN 3 THEN N' ON SCHEMA::' + QUOTENAME(SCHEMA_NAME(p.major_id))
                   WHEN 4 THEN N' ON ' + CASE t.type WHEN 'R' THEN N'ROLE::' WHEN 'A' THEN N'APPLICATION ROLE::' ELSE N'USER::' END
                             + QUOTENAME(t.name)
                   ELSE N''
                 END
               + N' FROM ' + QUOTENAME(u.name) + N' CASCADE;' + NCHAR(10)
FROM sys.database_permissions AS p
JOIN sys.database_principals  AS u ON u.principal_id = p.grantee_principal_id
LEFT JOIN sys.columns         AS c ON p.class = 1 AND c.object_id = p.major_id AND c.column_id = p.minor_id
LEFT JOIN sys.database_principals AS t ON p.class = 4 AND t.principal_id = p.major_id
WHERE p.class IN (0, 1, 3, 4)
  AND NOT (p.class = 0 AND p.permission_name = N'CONNECT')
  AND u.name IN (N'sqlagent_sales_rep', N'sqlagent_finance', N'sqlagent_admin', N'sqlagent_app');

IF @reset <> N'' EXEC (@reset);
GO

-- Baseline for sqlagent_app, which owns no data here: metadata only (sys.tables, sys.columns, extended
-- properties for schema introspection), no SELECT on any table. Its AuditLog rights live in the SqlAgent database.
GRANT VIEW DEFINITION TO sqlagent_app;
GO

-- Row-level-secured tables: every role may read them; the policy decides which rows.
GRANT SELECT ON Sales.SalesOrderHeader            TO sqlagent_sales_rep, sqlagent_finance, sqlagent_admin;
GRANT SELECT ON Sales.SalesOrderDetail            TO sqlagent_sales_rep, sqlagent_finance, sqlagent_admin;
GRANT SELECT ON Sales.SalesOrderHeaderSalesReason TO sqlagent_sales_rep, sqlagent_finance, sqlagent_admin;
GRANT SELECT ON Sales.Customer                    TO sqlagent_sales_rep, sqlagent_finance, sqlagent_admin;
GRANT SELECT ON Sales.Store                       TO sqlagent_sales_rep, sqlagent_finance, sqlagent_admin;

-- Territory table: a sales rep gets the identifying columns only, never the pre-aggregated
-- SalesYTD / SalesLastYear / CostYTD / CostLastYear (they would show every territory's revenue).
GRANT SELECT ON Sales.SalesTerritory (TerritoryID, [Name], CountryRegionCode, [Group]) TO sqlagent_sales_rep;
GRANT SELECT ON Sales.SalesTerritory TO sqlagent_finance, sqlagent_admin;

-- Explicit DENY as a backstop: it wins over any table-level GRANT a later edit might add by mistake.
DENY SELECT ON Sales.SalesTerritory (SalesYTD, SalesLastYear, CostYTD, CostLastYear) TO sqlagent_sales_rep;
GO

COMMIT TRANSACTION;
GO
