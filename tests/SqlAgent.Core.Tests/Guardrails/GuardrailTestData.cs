using SqlAgent.Core.Schema;

namespace SqlAgent.Core.Tests.Guardrails;

/// <summary>
/// The allow lists the guardrail tests run against: built from the shipped semantic.yaml, over a schema
/// synthesized from that same file, so no database is needed and the tests cannot drift from the real rules.
/// </summary>
internal static class GuardrailTestData
{
    private static readonly Lazy<SchemaCatalog> LazyCatalog = new(BuildCatalog);

    public static SchemaCatalog Catalog => LazyCatalog.Value;

    public static AllowList AllowList(Role role) => Catalog.GetAllowList(role);

    private static SchemaCatalog BuildCatalog()
    {
        var config = SemanticConfigParser.Load(new AgentAssetsOptions());
        return SemanticLayerLoader.Build(config, SyntheticSchema(config));
    }

    /// <summary>
    /// Every table the file mentions, with the columns the file itself names (synonyms, literal deny entries),
    /// plus Person.Password so that the "Person.*: Password*" pattern has something to match.
    /// </summary>
    private static DatabaseSchema SyntheticSchema(SemanticConfig config)
    {
        var columns = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        HashSet<string> For(string table)
        {
            var key = NameNormalizer.Normalize(table);
            if (!columns.TryGetValue(key, out var set)) columns[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Id" };
            return set;
        }

        foreach (var table in config.Roles.Values.SelectMany(r => r.Tables)) For(table);
        foreach (var (table, meta) in config.Tables) For(table).UnionWith(meta.Synonyms.Values);
        foreach (var hint in config.JoinHints) foreach (var table in hint.Tables) For(table);

        var rules = config.GlobalDenyColumns.Concat(config.Roles.Values.SelectMany(r => r.DenyColumns));
        foreach (var rule in rules.Where(r => !r.IsWildcard)) For(rule.TableKey).UnionWith(rule.ColumnPatterns);

        For("Person.Password").UnionWith(["PasswordHash", "PasswordSalt"]);

        return new DatabaseSchema(columns.Select(kv =>
        {
            var parts = NameNormalizer.SplitParts(kv.Key);
            return new TableInfo(parts[0], parts[1], null, kv.Value.Select(c => new ColumnInfo(c, "int", false, null)).ToList(), []);
        }));
    }
}
