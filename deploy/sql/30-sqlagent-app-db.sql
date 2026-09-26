-- Application database: bootstrap state and the audit log. Idempotent.

USE master;
GO
IF DB_ID(N'SqlAgent') IS NULL
    CREATE DATABASE SqlAgent;
GO

USE SqlAgent;
GO

-- The entrypoint flips Ready to 0 at the start of every boot and back to 1 after all scripts ran;
-- the container healthcheck reads it. RestoreDone stops a restart from re-restoring the database.
IF OBJECT_ID(N'dbo.BootstrapState') IS NULL
    CREATE TABLE dbo.BootstrapState (
        Version     int          NOT NULL PRIMARY KEY,
        RestoreDone bit          NOT NULL CONSTRAINT DF_BootstrapState_RestoreDone DEFAULT 0,
        Ready       bit          NOT NULL CONSTRAINT DF_BootstrapState_Ready DEFAULT 0,
        UpdatedUtc  datetime2    NOT NULL CONSTRAINT DF_BootstrapState_UpdatedUtc DEFAULT SYSUTCDATETIME()
    );

IF NOT EXISTS (SELECT 1 FROM dbo.BootstrapState WHERE Version = 1)
    INSERT dbo.BootstrapState (Version) VALUES (1);

-- One row per run_sql call the agent makes (allowed or blocked). Written by the API in a later phase.
IF OBJECT_ID(N'dbo.AuditLog') IS NULL
    CREATE TABLE dbo.AuditLog (
        Id             bigint         IDENTITY(1,1) NOT NULL PRIMARY KEY,
        OccurredUtc    datetime2      NOT NULL CONSTRAINT DF_AuditLog_OccurredUtc DEFAULT SYSUTCDATETIME(),
        Sub            nvarchar(100)  NOT NULL,
        [Role]         nvarchar(30)   NOT NULL,
        ConversationId uniqueidentifier NULL,
        Question       nvarchar(2000) NULL,
        [Sql]          nvarchar(max)  NOT NULL,
        Allowed        bit            NOT NULL,
        Violations     nvarchar(max)  NULL,
        ReturnedRows   int            NULL,
        ElapsedMs      int            NULL,
        TraceId        varchar(32)    NULL
    );

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'sqlagent_app')
    CREATE USER sqlagent_app FOR LOGIN sqlagent_app;
ELSE
    ALTER USER sqlagent_app WITH LOGIN = sqlagent_app;

-- Append-only for the application: it can add and read audit rows, never change or delete them.
GRANT SELECT, INSERT ON dbo.AuditLog TO sqlagent_app;
DENY UPDATE, DELETE ON dbo.AuditLog TO sqlagent_app;
GO
