-- Database-scoped settings for AdventureWorks2022. Idempotent.
--
-- MAXDOP = 1: Microsoft documents that SESSION_CONTEXT can return wrong results or crash inside a
-- parallel plan when the session was reset for reuse (SQL Server 2019 CU14+). This stack reads
-- SESSION_CONTEXT in its row-level-security predicates over pooled connections, which is exactly
-- that setup. Serial plans avoid the issue, and also cap the CPU any single generated query can take.
-- If analytical queries prove too slow, switch to trace flag 11042 instead.

USE AdventureWorks2022;
GO
ALTER DATABASE SCOPED CONFIGURATION SET MAXDOP = 1;
GO
