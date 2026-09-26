namespace SqlAgent.Core.Schema;

/// <summary>
/// The catalog built from introspection plus semantic.yaml. Everything is computed once by
/// <see cref="SemanticLayerLoader"/>; after that it is read-only and safe to share across requests.
/// </summary>
public sealed class SchemaCatalog : ISchemaCatalog
{
    private readonly IReadOnlyDictionary<Role, RoleView> _views;

    internal SchemaCatalog(IReadOnlyDictionary<Role, RoleView> views) => _views = views;

    /// <summary>The single reply for a name that is unknown or denied, so a response never confirms a denied table exists.</summary>
    public static string NotAvailableMessage(string name) =>
        $"Table '{name}' was not found or is not available. Call list_tables to see the available tables.";

    public IReadOnlyList<TableSummary> ListTables(Role role) => _views[role].Summaries;

    public AllowList GetAllowList(Role role) => _views[role].AllowList;

    public IReadOnlyList<SqlExample> GetExamples(Role role) => _views[role].Examples;

    public DescribeTablesResult DescribeTables(Role role, IEnumerable<string> names)
    {
        var view = _views[role];
        var found = new List<TableDescription>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            // The names come from a model's tool call: a null or blank one is just "not available".
            var key = string.IsNullOrWhiteSpace(name) ? null : NameNormalizer.Normalize(name);
            if (key is null || !view.Descriptions.TryGetValue(key, out var description)) errors.Add(NotAvailableMessage(name ?? ""));
            else if (seen.Add(key)) found.Add(description);
        }

        return new DescribeTablesResult(found, errors);
    }

    /// <summary>Everything one role can see, prebuilt so lookups never touch the unfiltered schema.</summary>
    internal sealed record RoleView(
        IReadOnlyList<TableSummary> Summaries,
        IReadOnlyDictionary<string, TableDescription> Descriptions,
        AllowList AllowList,
        IReadOnlyList<SqlExample> Examples);
}
