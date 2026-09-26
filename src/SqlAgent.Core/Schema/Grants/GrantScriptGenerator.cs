using System.Text;

namespace SqlAgent.Core.Schema;

/// <summary>
/// Turns the semantic layer into <c>deploy/sql/40-role-grants.generated.sql</c>, so the database enforces the same
/// allowlist as the guardrail. Needs no database: a deny entry with a '*' or '?' is written as a small block
/// that expands against sys.columns when the script runs, which keeps the output deterministic for the CI drift check.
/// </summary>
public static class GrantScriptGenerator
{
    // Convergent: everything the reader users and sqlagent_app hold beyond public is revoked first (in the same
    // transaction), so a grant removed from semantic.yaml also disappears from an existing volume. CONNECT stays.
    private const string ResetPreamble = """
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
        """;

    private const string WildcardExpansion = """
        DECLARE @deny nvarchar(max) = N'';
        SELECT @deny += N'DENY SELECT ON ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name)
                      + N' (' + QUOTENAME(c.name) + N') TO ' + QUOTENAME(w.Grantee) + N';' + NCHAR(10)
        FROM @wild AS w
        JOIN sys.schemas AS s ON s.name COLLATE Latin1_General_100_CI_AS LIKE w.SchemaPattern ESCAPE N'\'
        JOIN sys.tables  AS t ON t.schema_id = s.schema_id AND t.name COLLATE Latin1_General_100_CI_AS LIKE w.TablePattern ESCAPE N'\'
        JOIN sys.columns AS c ON c.object_id = t.object_id AND c.name COLLATE Latin1_General_100_CI_AS LIKE w.ColumnPattern ESCAPE N'\';
        IF @deny <> N'' EXEC (@deny);
        """;

    public static string Generate(SemanticConfig config)
    {
        var script = new StringBuilder();
        script.AppendLine("-- GENERATED from SqlAgent.Core/assets/semantic.yaml by `dotnet run --project src/SqlAgent.Cli -- emit-grants`.");
        script.AppendLine("-- Do not edit by hand: CI regenerates this file and fails on any difference.");
        script.AppendLine("--");
        script.AppendLine("-- Convergent, not add-only: it first strips everything the three reader users and sqlagent_app hold beyond public,");
        script.AppendLine("-- then grants the intended set, so a grant removed from semantic.yaml also disappears from an existing volume.");
        script.AppendLine("-- CONNECT is kept: without it nobody can log in. One transaction, so a reader is never left without its");
        script.AppendLine("-- grants after a partial failure. Table-level denial is absence from a role's tables; denied columns get an explicit DENY,");
        script.AppendLine("-- which wins over the table-level GRANT. Only base tables are granted (see the ownership-chaining note in docs/rls-coverage.md).");
        script.AppendLine();
        script.AppendLine("USE AdventureWorks2022;");
        script.AppendLine("GO");
        script.AppendLine("SET XACT_ABORT ON;");
        script.AppendLine("GO");
        script.AppendLine("BEGIN TRANSACTION;");
        script.AppendLine("GO");
        script.AppendLine();
        script.AppendLine(ResetPreamble);
        script.AppendLine("GO");
        script.AppendLine();
        script.AppendLine("-- sqlagent_app owns no data here: metadata only (sys.tables, sys.columns, extended properties for schema");
        script.AppendLine("-- introspection), no SELECT on any table. Its AuditLog rights live in the SqlAgent database.");
        script.AppendLine("GRANT VIEW DEFINITION TO sqlagent_app;");
        script.AppendLine("GO");

        var wildcardRows = new List<string>();
        foreach (var role in RoleExtensions.All)
        {
            var roleConfig = config.Roles[role];
            var user = Quote(role.ToDbUser());
            var tables = roleConfig.Tables.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();

            script.AppendLine();
            script.AppendLine($"-- {role.ToKey()}: {tables.Count} tables");
            foreach (var table in tables) script.AppendLine($"GRANT SELECT ON {QuoteTable(table)} TO {user};");

            var denyRules = config.GlobalDenyColumns.Concat(roleConfig.DenyColumns).ToList();
            var explicitDenies = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            var roleWildcards = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var rule in denyRules)
            {
                if (rule.IsWildcard)
                {
                    foreach (var column in rule.ColumnPatterns)
                    {
                        roleWildcards.Add($"(N'{role.ToDbUser()}', N'{Like(rule.SchemaPattern)}', N'{Like(rule.TablePattern)}', N'{Like(column)}')");
                    }

                    continue;
                }

                // A concrete deny only matters for a table the role can read.
                var table = NameNormalizer.Table(rule.SchemaPattern, rule.TablePattern);
                if (!tables.Contains(table, StringComparer.OrdinalIgnoreCase)) continue;
                if (!explicitDenies.TryGetValue(table, out var columns))
                    explicitDenies[table] = columns = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                columns.UnionWith(rule.ColumnPatterns);
            }

            if (explicitDenies.Count > 0)
            {
                script.AppendLine($"-- {role.ToKey()}: denied columns (backstop that wins over the table-level GRANT)");
                foreach (var (table, columns) in explicitDenies)
                    script.AppendLine($"DENY SELECT ON {QuoteTable(table)} ({string.Join(", ", columns.Select(Quote))}) TO {user};");
            }

            wildcardRows.AddRange(roleWildcards);
        }

        if (wildcardRows.Count > 0)
        {
            script.AppendLine();
            script.AppendLine("-- Pattern denies (a '*' or '?' in semantic.yaml), expanded against the real columns when this script runs.");
            script.AppendLine("-- LIKE with '\\' as the escape character; '*' became '%' and '?' became '_'.");
            script.AppendLine("-- Matching is case-insensitive whatever the database collation, like the glob matching in the catalog.");
            script.AppendLine("DECLARE @wild TABLE (Grantee sysname, SchemaPattern nvarchar(128), TablePattern nvarchar(128), ColumnPattern nvarchar(128));");
            script.AppendLine("INSERT @wild (Grantee, SchemaPattern, TablePattern, ColumnPattern) VALUES");
            script.AppendLine("  " + string.Join(",\n  ", wildcardRows) + ";");
            script.AppendLine(WildcardExpansion);
        }

        script.AppendLine("GO");
        script.AppendLine();
        script.AppendLine("COMMIT TRANSACTION;");
        script.AppendLine("GO");

        // The raw string literals above carry the source file's line endings; the output is always LF.
        return script.ToString().Replace("\r\n", "\n");
    }

    private static string Quote(string identifier) => "[" + identifier + "]";

    private static string QuoteTable(string normalizedTable) =>
        string.Join('.', NameNormalizer.SplitParts(normalizedTable).Select(Quote));

    private static string Like(string pattern) => GlobPattern.ToLikePattern(pattern);
}
