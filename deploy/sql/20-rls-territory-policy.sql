-- Row-level security: a sales rep sees only rows of the territory in SESSION_CONTEXT('territory_id').
-- Idempotent. The full table-by-table classification lives in docs/rls-coverage.md.
--
-- Who is exempt: the finance and admin DB users, and dbo (used by the bootstrap and by sa). The exemption
-- is decided by the DATABASE USER, never by a session-context value, so no query can grant itself a role.
-- A sales rep with no territory in the context sees no rows: NULL never equals a TerritoryID.
--
-- The predicates read SESSION_CONTEXT, which the API sets read-only right after opening the connection.
-- The ownership chain (everything here is owned by dbo) lets the predicates read the tables they need
-- without granting the sales rep SELECT on them.

USE AdventureWorks2022;
GO

IF SCHEMA_ID(N'Security') IS NULL
    EXEC (N'CREATE SCHEMA Security');
GO

-- One transaction from the drop to the re-create: DDL is transactional, so at no point can another
-- session see the tables without the policy (this script also runs by hand against a live database).
-- XACT_ABORT rolls everything back on any error; sqlcmd -b then stops with a non-zero exit.
SET XACT_ABORT ON;
GO
BEGIN TRANSACTION;
GO

-- The policy is dropped first: SCHEMABINDING would otherwise block re-creating the functions it uses.
IF EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'TerritoryPolicy' AND schema_id = SCHEMA_ID(N'Security'))
    DROP SECURITY POLICY Security.TerritoryPolicy;
GO

-- Tables that carry a TerritoryID column.
CREATE OR ALTER FUNCTION Security.fn_can_see_territory(@TerritoryID int)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS allowed
    WHERE USER_NAME() IN (N'dbo', N'sqlagent_finance', N'sqlagent_admin')
       OR @TerritoryID = CAST(SESSION_CONTEXT(N'territory_id') AS int);
GO

-- Order child tables (no TerritoryID of their own): follow the order header's territory.
CREATE OR ALTER FUNCTION Security.fn_can_see_order(@SalesOrderID int)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS allowed
    WHERE USER_NAME() IN (N'dbo', N'sqlagent_finance', N'sqlagent_admin')
       OR EXISTS (
            SELECT 1
            FROM Sales.SalesOrderHeader AS h
            WHERE h.SalesOrderID = @SalesOrderID
              AND h.TerritoryID = CAST(SESSION_CONTEXT(N'territory_id') AS int));
GO

-- Stores belong to a territory through their salesperson. A store with no salesperson is hidden.
CREATE OR ALTER FUNCTION Security.fn_can_see_salesperson_territory(@SalesPersonID int)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS allowed
    WHERE USER_NAME() IN (N'dbo', N'sqlagent_finance', N'sqlagent_admin')
       OR EXISTS (
            SELECT 1
            FROM Sales.SalesPerson AS sp
            WHERE sp.BusinessEntityID = @SalesPersonID
              AND sp.TerritoryID = CAST(SESSION_CONTEXT(N'territory_id') AS int));
GO

CREATE SECURITY POLICY Security.TerritoryPolicy
    ADD FILTER PREDICATE Security.fn_can_see_territory(TerritoryID)                ON Sales.SalesOrderHeader,
    ADD FILTER PREDICATE Security.fn_can_see_territory(TerritoryID)                ON Sales.Customer,
    ADD FILTER PREDICATE Security.fn_can_see_order(SalesOrderID)                   ON Sales.SalesOrderDetail,
    ADD FILTER PREDICATE Security.fn_can_see_order(SalesOrderID)                   ON Sales.SalesOrderHeaderSalesReason,
    ADD FILTER PREDICATE Security.fn_can_see_salesperson_territory(SalesPersonID)  ON Sales.Store
    WITH (STATE = ON);
GO

COMMIT TRANSACTION;
GO
