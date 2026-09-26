using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlAgent.Core.Schema;

/// <summary>
/// Checks that an example query only touches what its role may read. Until the guardrail exists this is the
/// stand-in: the SQL must parse as one SELECT, use only allowlisted 2-part tables, list its columns explicitly,
/// and name no column that is denied on any table it reads (a conservative name match, no alias resolution).
/// </summary>
internal static class ExampleSqlValidator
{
    /// <param name="deniedColumns">Denied columns per table, or null to check tables only (offline validation).</param>
    public static IReadOnlyList<string> Validate(
        string sql,
        IReadOnlySet<string> allowedTables,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? deniedColumns)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var script = parser.Parse(reader, out var parseErrors);
        if (parseErrors.Count > 0)
        {
            return [$"does not parse: {parseErrors[0].Message} (line {parseErrors[0].Line})"];
        }

        if (script is not TSqlScript { Batches: [{ Statements: [SelectStatement select] }] })
        {
            return ["must be exactly one SELECT statement"];
        }

        var scan = new Scan();
        select.Accept(scan);

        var errors = new List<string>();
        if (scan.HasSelectStar) errors.Add("uses SELECT * or t.*; list the columns explicitly");
        if (scan.HasUnsupportedConstruct)
            errors.Add("uses a function call with a qualified name, OPENROWSET/OPENQUERY, a table-valued function or SELECT ... INTO; only plain queries over allowlisted tables are allowed");

        var readTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in scan.Tables)
        {
            var parts = table.Parts;
            if (parts.Count == 1 && scan.CteNames.Contains(parts[0])) continue;

            if (parts.Count != 2)
            {
                errors.Add($"table '{table.Text}' must be a 2-part name (schema.table)");
                continue;
            }

            var name = NameNormalizer.Table(parts[0], parts[1]);
            readTables.Add(name);
            if (!allowedTables.Contains(name)) errors.Add($"table {name} is not in the role's allowlist");
        }

        if (deniedColumns is not null)
        {
            var denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in readTables)
            {
                if (deniedColumns.TryGetValue(table, out var columns)) denied.UnionWith(columns);
            }

            foreach (var column in scan.ColumnNames.Where(denied.Contains).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"references column '{column}', which is denied on a table it reads");
            }
        }

        return errors;
    }

    private sealed record TableRef(IReadOnlyList<string> Parts)
    {
        public string Text => string.Join('.', Parts);
    }

    private sealed class Scan : TSqlFragmentVisitor
    {
        public List<TableRef> Tables { get; } = [];
        public HashSet<string> CteNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> ColumnNames { get; } = [];
        public bool HasSelectStar { get; private set; }

        public override void Visit(NamedTableReference node)
        {
            var name = node.SchemaObject;
            var parts = new List<string>();
            if (name.ServerIdentifier is not null) parts.Add(name.ServerIdentifier.Value);
            if (name.DatabaseIdentifier is not null) parts.Add(name.DatabaseIdentifier.Value);
            if (name.SchemaIdentifier is not null) parts.Add(name.SchemaIdentifier.Value);
            parts.Add(name.BaseIdentifier.Value);
            Tables.Add(new TableRef(parts));
        }

        // Table-valued functions, OPENROWSET/OPENQUERY, SELECT ... INTO and qualified function calls can reach
        // beyond the allowlisted base tables (an ownership chain, another server, a new table), so none is accepted.
        public bool HasUnsupportedConstruct { get; private set; }

        public override void Visit(SchemaObjectFunctionTableReference node) => HasUnsupportedConstruct = true;
        public override void Visit(OpenRowsetTableReference node) => HasUnsupportedConstruct = true;
        public override void Visit(OpenQueryTableReference node) => HasUnsupportedConstruct = true;
        public override void Visit(AdHocTableReference node) => HasUnsupportedConstruct = true;
        public override void Visit(FunctionCall node) { if (node.CallTarget is not null) HasUnsupportedConstruct = true; }
        public override void Visit(SelectStatement node) { if (node.Into is not null) HasUnsupportedConstruct = true; }

        public override void Visit(CommonTableExpression node) => CteNames.Add(node.ExpressionName.Value);

        // COUNT(*) is a wildcard column reference (no identifier); SELECT * and t.* are SelectStarExpression.
        public override void Visit(SelectStarExpression node) => HasSelectStar = true;

        public override void Visit(ColumnReferenceExpression node)
        {
            var identifiers = node.MultiPartIdentifier?.Identifiers;
            if (identifiers is { Count: > 0 }) ColumnNames.Add(identifiers[^1].Value);
        }
    }
}
