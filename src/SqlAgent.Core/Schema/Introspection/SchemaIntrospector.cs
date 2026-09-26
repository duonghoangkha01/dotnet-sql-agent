using Dapper;
using Microsoft.Data.SqlClient;

namespace SqlAgent.Core.Schema;

/// <summary>
/// Reads base tables, columns, foreign keys and MS_Description properties in one round trip
/// (three result sets). Runs as sqlagent_app, which holds VIEW DEFINITION and no SELECT on data.
/// </summary>
public sealed class SchemaIntrospector
{
    // User-defined alias types (Name, Flag, Phone, ...) are shown as their base type, which is what the model
    // needs to write correct SQL. CLR types (hierarchyid, geography) keep their own name.
    private const string IntrospectionSql = """
        SELECT s.name AS SchemaName, t.name AS TableName,
               CAST(ep.value AS nvarchar(1000)) AS Description
        FROM sys.tables AS t
        JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        LEFT JOIN sys.extended_properties AS ep
               ON ep.class = 1 AND ep.major_id = t.object_id AND ep.minor_id = 0 AND ep.name = N'MS_Description'
        WHERE t.is_ms_shipped = 0
        ORDER BY s.name, t.name;

        SELECT s.name AS SchemaName, t.name AS TableName, c.name AS ColumnName, c.is_nullable AS IsNullable,
               b.BaseType
             + CASE
                 WHEN b.BaseType IN (N'char', N'varchar', N'binary', N'varbinary')
                   THEN N'(' + CASE WHEN c.max_length = -1 THEN N'max' ELSE CAST(c.max_length AS nvarchar(10)) END + N')'
                 WHEN b.BaseType IN (N'nchar', N'nvarchar')
                   THEN N'(' + CASE WHEN c.max_length = -1 THEN N'max' ELSE CAST(c.max_length / 2 AS nvarchar(10)) END + N')'
                 WHEN b.BaseType IN (N'decimal', N'numeric')
                   THEN N'(' + CAST(c.precision AS nvarchar(10)) + N',' + CAST(c.scale AS nvarchar(10)) + N')'
                 WHEN b.BaseType IN (N'datetime2', N'time', N'datetimeoffset')
                   THEN N'(' + CAST(c.scale AS nvarchar(10)) + N')'
                 ELSE N''
               END AS DataType,
               CAST(ep.value AS nvarchar(1000)) AS Description
        FROM sys.tables AS t
        JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        JOIN sys.columns AS c ON c.object_id = t.object_id
        JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
        CROSS APPLY (SELECT CASE WHEN ty.is_user_defined = 1 AND ty.is_assembly_type = 0
                                 THEN TYPE_NAME(c.system_type_id) ELSE ty.name END AS BaseType) AS b
        LEFT JOIN sys.extended_properties AS ep
               ON ep.class = 1 AND ep.major_id = t.object_id AND ep.minor_id = c.column_id AND ep.name = N'MS_Description'
        WHERE t.is_ms_shipped = 0
        ORDER BY s.name, t.name, c.column_id;

        SELECT fk.name AS ForeignKeyName,
               ps.name AS SchemaName, pt.name AS TableName, pc.name AS ColumnName,
               rs.name AS ReferencedSchema, rt.name AS ReferencedTable, rc.name AS ReferencedColumn
        FROM sys.foreign_keys AS fk
        JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.tables  AS pt ON pt.object_id = fk.parent_object_id
        JOIN sys.schemas AS ps ON ps.schema_id = pt.schema_id
        JOIN sys.columns AS pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.tables  AS rt ON rt.object_id = fk.referenced_object_id
        JOIN sys.schemas AS rs ON rs.schema_id = rt.schema_id
        JOIN sys.columns AS rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        WHERE pt.is_ms_shipped = 0
        ORDER BY ps.name, pt.name, fk.name, fkc.constraint_column_id;
        """;

    public async Task<DatabaseSchema> IntrospectAsync(string connectionString, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        using var results = await connection.QueryMultipleAsync(new CommandDefinition(IntrospectionSql, cancellationToken: ct));
        var tables = (await results.ReadAsync<TableRow>()).ToList();
        var columns = (await results.ReadAsync<ColumnRow>()).ToLookup(c => NameNormalizer.Table(c.SchemaName, c.TableName), StringComparer.OrdinalIgnoreCase);
        var foreignKeys = (await results.ReadAsync<ForeignKeyRow>()).ToLookup(f => NameNormalizer.Table(f.SchemaName, f.TableName), StringComparer.OrdinalIgnoreCase);

        return new DatabaseSchema(tables.Select(t =>
        {
            var key = NameNormalizer.Table(t.SchemaName, t.TableName);
            return new TableInfo(
                t.SchemaName,
                t.TableName,
                Clean(t.Description),
                columns[key].Select(c => new ColumnInfo(c.ColumnName, c.DataType, c.IsNullable, Clean(c.Description))).ToList(),
                foreignKeys[key]
                    .GroupBy(f => f.ForeignKeyName)
                    .Select(g => new ForeignKeyInfo(
                        g.Key,
                        g.Select(f => f.ColumnName).ToList(),
                        NameNormalizer.Table(g.First().ReferencedSchema, g.First().ReferencedTable),
                        g.Select(f => f.ReferencedColumn).ToList()))
                    .ToList());
        }));
    }

    private static string? Clean(string? description) => string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    // Dapper maps result columns to these by name.
    private sealed class TableRow
    {
        public string SchemaName { get; set; } = "";
        public string TableName { get; set; } = "";
        public string? Description { get; set; }
    }

    private sealed class ColumnRow
    {
        public string SchemaName { get; set; } = "";
        public string TableName { get; set; } = "";
        public string ColumnName { get; set; } = "";
        public bool IsNullable { get; set; }
        public string DataType { get; set; } = "";
        public string? Description { get; set; }
    }

    private sealed class ForeignKeyRow
    {
        public string ForeignKeyName { get; set; } = "";
        public string SchemaName { get; set; } = "";
        public string TableName { get; set; } = "";
        public string ColumnName { get; set; } = "";
        public string ReferencedSchema { get; set; } = "";
        public string ReferencedTable { get; set; } = "";
        public string ReferencedColumn { get; set; } = "";
    }
}
