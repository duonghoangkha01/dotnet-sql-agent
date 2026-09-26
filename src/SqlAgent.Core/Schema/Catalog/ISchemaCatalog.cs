namespace SqlAgent.Core.Schema;

/// <summary>
/// The agent's role-filtered view of the database. Behind an interface so tests can inject a stub
/// without a database or a semantic.yaml.
/// </summary>
public interface ISchemaCatalog
{
    /// <summary>Compact list (name and one-line description) of the tables the role may read.</summary>
    IReadOnlyList<TableSummary> ListTables(Role role);

    /// <summary>
    /// Columns, types, foreign keys and hints for the named tables, with denied columns removed. A table that
    /// does not exist and a table the role may not read produce the same error text.
    /// </summary>
    DescribeTablesResult DescribeTables(Role role, IEnumerable<string> names);

    AllowList GetAllowList(Role role);

    /// <summary>Few-shot examples tagged for the role.</summary>
    IReadOnlyList<SqlExample> GetExamples(Role role);
}

public sealed record TableSummary(string Name, string? Description);

public sealed record ColumnDescription(string Name, string DataType, bool IsNullable, string? Description);

public sealed record ForeignKeyDescription(
    IReadOnlyList<string> Columns,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns);

public sealed record TableDescription(
    string Name,
    string? Description,
    IReadOnlyList<ColumnDescription> Columns,
    IReadOnlyList<ForeignKeyDescription> ForeignKeys,
    IReadOnlyDictionary<string, string> Synonyms,
    IReadOnlyList<string> JoinHints);

/// <param name="Tables">Descriptions of the names that were found and are readable.</param>
/// <param name="Errors">One message per name that was not; it never says whether the table exists.</param>
public sealed record DescribeTablesResult(IReadOnlyList<TableDescription> Tables, IReadOnlyList<string> Errors);
