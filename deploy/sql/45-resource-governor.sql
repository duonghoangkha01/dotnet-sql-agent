-- Resource Governor: one workload group for the three reader users. Idempotent: safe to re-run on every boot.
--
-- The guardrail refuses OPTION (MAXDOP n) and the executor cancels a query after 15 s, but both live in the
-- application. This makes the database enforce a ceiling on what one generated query can take, whatever
-- reaches it: memory grant, CPU time and parallelism (MAX_DOP here overrides an OPTION (MAXDOP n) hint).
-- Sessions are classified by ORIGINAL_LOGIN(), so EXECUTE AS cannot move a reader into another group.
--
-- REQUEST_MAX_CPU_TIME_SEC only raises an event unless trace flag 2422 is on; with it the request is aborted.
-- The flag does not survive a restart, which is why it is set here, by a script that runs on every boot.

USE master;
GO

-- The classifier is the only thing that may be bound. Refuse to replace someone else's.
IF EXISTS (
    SELECT 1
    FROM sys.resource_governor_configuration AS c
    JOIN sys.objects AS o ON o.object_id = c.classifier_function_id
    WHERE o.name <> N'sqlagent_rg_classifier')
    THROW 50000, N'Resource Governor already uses another classifier function; not replacing it.', 1;
GO

-- A bound classifier cannot be altered: unbind ours first (readers fall into the default group only for this moment).
IF EXISTS (
    SELECT 1
    FROM sys.resource_governor_configuration AS c
    JOIN sys.objects AS o ON o.object_id = c.classifier_function_id
    WHERE o.name = N'sqlagent_rg_classifier')
BEGIN
    ALTER RESOURCE GOVERNOR WITH (CLASSIFIER_FUNCTION = NULL);
    ALTER RESOURCE GOVERNOR RECONFIGURE;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.resource_governor_workload_groups WHERE name = N'sqlagent_readers')
    CREATE WORKLOAD GROUP sqlagent_readers
    WITH (
        REQUEST_MAX_MEMORY_GRANT_PERCENT = 10,  -- of the pool; the default is 25, and a sort or hash join takes a grant
        REQUEST_MAX_CPU_TIME_SEC = 10,          -- below the executor's 15 s wall-clock limit
        MAX_DOP = 1)
    USING [default];
ELSE
    ALTER WORKLOAD GROUP sqlagent_readers
    WITH (
        REQUEST_MAX_MEMORY_GRANT_PERCENT = 10,
        REQUEST_MAX_CPU_TIME_SEC = 10,
        MAX_DOP = 1);
GO

CREATE OR ALTER FUNCTION dbo.sqlagent_rg_classifier()
RETURNS sysname
WITH SCHEMABINDING
AS
BEGIN
    RETURN CASE
        WHEN ORIGINAL_LOGIN() IN (N'sqlagent_sales_rep', N'sqlagent_finance', N'sqlagent_admin') THEN N'sqlagent_readers'
        ELSE N'default'
    END;
END;
GO

ALTER RESOURCE GOVERNOR WITH (CLASSIFIER_FUNCTION = dbo.sqlagent_rg_classifier);
ALTER RESOURCE GOVERNOR RECONFIGURE;
GO

DBCC TRACEON (2422, -1) WITH NO_INFOMSGS;
GO
