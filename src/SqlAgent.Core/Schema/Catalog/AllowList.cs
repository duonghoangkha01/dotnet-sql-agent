namespace SqlAgent.Core.Schema;

/// <summary>
/// What one role may read: the contract between the schema catalog, the guardrail (default-deny) and the
/// generated database grants. All three are built from the same semantic.yaml.
/// </summary>
/// <param name="Tables">Normalized "schema.table" names (see <see cref="NameNormalizer"/>), case-insensitive.
/// Base tables only: views and table-valued functions are never listed.</param>
/// <param name="DeniedColumns">Per normalized table, the columns no query may name. Globs are already expanded
/// and the global deny list is merged in. Case-insensitive on both levels.</param>
public sealed record AllowList(
    IReadOnlySet<string> Tables,
    IReadOnlyDictionary<string, IReadOnlySet<string>> DeniedColumns);
