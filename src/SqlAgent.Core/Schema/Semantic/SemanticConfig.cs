namespace SqlAgent.Core.Schema;

// The validated, immutable form of semantic.yaml. Names are normalized ("Schema.Table").

/// <summary>
/// One deny entry: a table pattern (schema and table, either may contain '*' or '?') and the column patterns
/// denied on the tables it matches.
/// </summary>
public sealed record DenyRule(string SchemaPattern, string TablePattern, IReadOnlyList<string> ColumnPatterns)
{
    /// <summary>True if any part needs expanding against the database instead of naming one column of one table.</summary>
    public bool IsWildcard => GlobPattern.HasWildcard(SchemaPattern) || GlobPattern.HasWildcard(TablePattern)
                              || ColumnPatterns.Any(GlobPattern.HasWildcard);

    public string TableKey => SchemaPattern + "." + TablePattern;
}

public sealed record RoleConfig(Role Role, IReadOnlyList<string> Tables, IReadOnlyList<DenyRule> DenyColumns);

public sealed record TableMeta(string? Description, IReadOnlyDictionary<string, string> Synonyms);

/// <summary>Free-text advice on how two or more tables join, shown only to roles that can see all of them.</summary>
public sealed record JoinHint(IReadOnlyList<string> Tables, string Text);

/// <summary>A question with a known-good query, shown to the agent of each listed role as a few-shot example.</summary>
public sealed record SqlExample(IReadOnlyList<Role> Roles, string Question, string Sql);

public sealed record SemanticConfig(
    IReadOnlyDictionary<Role, RoleConfig> Roles,
    IReadOnlyList<DenyRule> GlobalDenyColumns,
    IReadOnlyDictionary<string, TableMeta> Tables,
    IReadOnlyList<JoinHint> JoinHints,
    IReadOnlyList<SqlExample> Examples,
    string SourcePath);
