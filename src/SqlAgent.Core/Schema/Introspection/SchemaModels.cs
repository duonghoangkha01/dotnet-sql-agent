namespace SqlAgent.Core.Schema;

// What introspection finds in the database, before the semantic layer filters it per role.

public sealed record ColumnInfo(string Name, string DataType, bool IsNullable, string? Description);

/// <summary>A foreign key. Composite keys list their columns in ordinal order.</summary>
public sealed record ForeignKeyInfo(
    string Name,
    IReadOnlyList<string> Columns,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns);

public sealed record TableInfo(
    string Schema,
    string Name,
    string? Description,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<ForeignKeyInfo> ForeignKeys)
{
    /// <summary>Normalized "Schema.Table".</summary>
    public string FullName => NameNormalizer.Table(Schema, Name);
}

/// <summary>All base tables of the database with their columns and foreign keys.</summary>
public sealed class DatabaseSchema
{
    private readonly Dictionary<string, TableInfo> _byName;

    public DatabaseSchema(IEnumerable<TableInfo> tables)
    {
        Tables = tables.ToList();
        _byName = Tables.ToDictionary(t => t.FullName, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<TableInfo> Tables { get; }

    public bool TryGetTable(string normalizedName, out TableInfo table) => _byName.TryGetValue(normalizedName, out table!);
}
