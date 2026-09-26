-- GENERATED from SqlAgent.Core/assets/semantic.yaml by `dotnet run --project src/SqlAgent.Cli -- emit-grants`.
-- Do not edit by hand: CI regenerates this file and fails on any difference.
--
-- Convergent, not add-only: it first strips everything the three reader users and sqlagent_app hold beyond public,
-- then grants the intended set, so a grant removed from semantic.yaml also disappears from an existing volume.
-- CONNECT is kept: without it nobody can log in. One transaction, so a reader is never left without its
-- grants after a partial failure. Table-level denial is absence from a role's tables; denied columns get an explicit DENY,
-- which wins over the table-level GRANT. Only base tables are granted (see the ownership-chaining note in docs/rls-coverage.md).

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

-- sqlagent_app owns no data here: metadata only (sys.tables, sys.columns, extended properties for schema
-- introspection), no SELECT on any table. Its AuditLog rights live in the SqlAgent database.
GRANT VIEW DEFINITION TO sqlagent_app;
GO

-- sales_rep: 3 tables
GRANT SELECT ON [Person].[Person] TO [sqlagent_sales_rep];
GRANT SELECT ON [Sales].[SalesOrderHeader] TO [sqlagent_sales_rep];
GRANT SELECT ON [Sales].[SalesTerritory] TO [sqlagent_sales_rep];
-- sales_rep: denied columns (backstop that wins over the table-level GRANT)
DENY SELECT ON [Sales].[SalesTerritory] ([SalesLastYear], [SalesYTD]) TO [sqlagent_sales_rep];

-- finance: 3 tables
GRANT SELECT ON [Sales].[CreditCard] TO [sqlagent_finance];
GRANT SELECT ON [Sales].[SalesOrderHeader] TO [sqlagent_finance];
GRANT SELECT ON [Sales].[SalesTerritory] TO [sqlagent_finance];
-- finance: denied columns (backstop that wins over the table-level GRANT)
DENY SELECT ON [Sales].[CreditCard] ([CardNumber]) TO [sqlagent_finance];

-- admin: 5 tables
GRANT SELECT ON [HumanResources].[Employee] TO [sqlagent_admin];
GRANT SELECT ON [Person].[Person] TO [sqlagent_admin];
GRANT SELECT ON [Sales].[CreditCard] TO [sqlagent_admin];
GRANT SELECT ON [Sales].[SalesOrderHeader] TO [sqlagent_admin];
GRANT SELECT ON [Sales].[SalesTerritory] TO [sqlagent_admin];
-- admin: denied columns (backstop that wins over the table-level GRANT)
DENY SELECT ON [HumanResources].[Employee] ([NationalIDNumber]) TO [sqlagent_admin];
DENY SELECT ON [Sales].[CreditCard] ([CardNumber]) TO [sqlagent_admin];

-- Pattern denies (a '*' or '?' in semantic.yaml), expanded against the real columns when this script runs.
-- LIKE with '\' as the escape character; '*' became '%' and '?' became '_'.
-- Matching is case-insensitive whatever the database collation, like the glob matching in the catalog.
DECLARE @wild TABLE (Grantee sysname, SchemaPattern nvarchar(128), TablePattern nvarchar(128), ColumnPattern nvarchar(128));
INSERT @wild (Grantee, SchemaPattern, TablePattern, ColumnPattern) VALUES
  (N'sqlagent_sales_rep', N'Person', N'%', N'Password%'),
  (N'sqlagent_sales_rep', N'Person', N'Person', N'Demographics'),
  (N'sqlagent_sales_rep', N'Person', N'Person', N'Info%'),
  (N'sqlagent_finance', N'Person', N'%', N'Password%'),
  (N'sqlagent_admin', N'Person', N'%', N'Password%');
DECLARE @deny nvarchar(max) = N'';
SELECT @deny += N'DENY SELECT ON ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name)
              + N' (' + QUOTENAME(c.name) + N') TO ' + QUOTENAME(w.Grantee) + N';' + NCHAR(10)
FROM @wild AS w
JOIN sys.schemas AS s ON s.name COLLATE Latin1_General_100_CI_AS LIKE w.SchemaPattern ESCAPE N'\'
JOIN sys.tables  AS t ON t.schema_id = s.schema_id AND t.name COLLATE Latin1_General_100_CI_AS LIKE w.TablePattern ESCAPE N'\'
JOIN sys.columns AS c ON c.object_id = t.object_id AND c.name COLLATE Latin1_General_100_CI_AS LIKE w.ColumnPattern ESCAPE N'\';
IF @deny <> N'' EXEC (@deny);
GO

COMMIT TRANSACTION;
GO
