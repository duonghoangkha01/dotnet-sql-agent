using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlAgent.Core.Guardrails;

/// <summary>
/// The tables one SELECT can see, and the ones enclosing it (a subquery may correlate with its parent).
/// Built per <see cref="QuerySpecification"/> from its FROM clause, before any column in it is looked at.
/// </summary>
internal sealed class Scope
{
    /// <param name="Keys">The names a column can be qualified with: the alias, or for an unaliased table its own
    /// name (<c>Product</c> or <c>Production.Product</c>).</param>
    /// <param name="BaseTable">Normalized "Schema.Table" when the source is a named 2-part table, otherwise null
    /// (derived tables and CTEs can only expose columns their own query was allowed to name).</param>
    private sealed record Source(IReadOnlyList<string> Keys, string? BaseTable);

    private readonly List<Source> _sources = [];

    private Scope(Scope? parent) => Parent = parent;

    public Scope? Parent { get; }

    public static Scope Build(QuerySpecification query, Scope? parent)
    {
        var scope = new Scope(parent);
        if (query.FromClause is { } from)
        {
            foreach (var reference in from.TableReferences) scope.Collect(reference);
        }

        return scope;
    }

    private void Collect(TableReference reference)
    {
        switch (reference)
        {
            case NamedTableReference named:
                var name = named.SchemaObject;
                var isTwoPart = name.SchemaIdentifier is not null && name.DatabaseIdentifier is null && name.ServerIdentifier is null;
                var baseName = name.BaseIdentifier.Key();
                var fullName = isTwoPart ? name.SchemaIdentifier!.Key() + "." + baseName : null;
                if (named.Alias is { } alias) _sources.Add(new Source([alias.Key()], fullName));
                else _sources.Add(new Source(fullName is null ? [baseName] : [baseName, fullName], fullName));
                break;
            case QueryDerivedTable { Alias: { } derivedAlias }:
                _sources.Add(new Source([derivedAlias.Key()], null));
                break;
            case QualifiedJoin join:
                Collect(join.FirstTableReference);
                Collect(join.SecondTableReference);
                break;
            case UnqualifiedJoin join:
                Collect(join.FirstTableReference);
                Collect(join.SecondTableReference);
                break;
            case JoinParenthesisTableReference parenthesis:
                if (parenthesis.Join is not null) Collect(parenthesis.Join);
                break;
            // Any other table source is refused by the visitor; it contributes no names here.
        }
    }

    /// <summary>
    /// Checks a column reference (<c>col</c>, <c>alias.col</c> or <c>schema.table.col</c>) against the denied columns.
    /// An unqualified name is refused if any table in scope denies it: without full binding we cannot tell which
    /// table it would resolve to, and refusing too much is the safe error (the database column DENY is the backstop).
    /// </summary>
    public ColumnCheck Check(IList<Identifier> identifiers, IReadOnlyDictionary<string, IReadOnlySet<string>> deniedColumns)
    {
        var column = identifiers[^1].Key();

        if (identifiers.Count == 1)
        {
            for (var scope = this; scope is not null; scope = scope.Parent)
            {
                if (scope._sources.Any(s => Denies(deniedColumns, s.BaseTable, column))) return ColumnCheck.Denied;
            }

            return ColumnCheck.Ok;
        }

        if (identifiers.Count > 3) return ColumnCheck.UnknownQualifier;

        var qualifier = string.Join('.', identifiers.Take(identifiers.Count - 1).Select(i => i.Key()));
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            var matches = scope._sources.Where(s => s.Keys.Contains(qualifier, StringComparer.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) continue;
            return matches.Any(s => Denies(deniedColumns, s.BaseTable, column)) ? ColumnCheck.Denied : ColumnCheck.Ok;
        }

        return ColumnCheck.UnknownQualifier;
    }

    private static bool Denies(IReadOnlyDictionary<string, IReadOnlySet<string>> denied, string? table, string column) =>
        table is not null && denied.TryGetValue(table, out var columns) && columns.Contains(column);
}

internal enum ColumnCheck
{
    Ok,
    Denied,
    UnknownQualifier,
}

internal static class IdentifierExtensions
{
    /// <summary>
    /// The identifier as names are compared. SQL Server collations ignore trailing spaces, so <c>[SalesYTD ]</c> can
    /// resolve to the column <c>SalesYTD</c>; comparing without them keeps a padded name from dodging a deny rule.
    /// </summary>
    public static string Key(this Identifier identifier) => identifier.Value.TrimEnd(' ');
}
