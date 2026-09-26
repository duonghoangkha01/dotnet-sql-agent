using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlAgent.Core.Schema;

/// <summary>
/// Parses semantic.yaml and validates everything that can be checked without a database: structure, the three
/// roles, name syntax, and that each example's tables are in the allowlist of every role it is tagged with.
/// Checks against the real schema (existence, glob expansion) are <see cref="SemanticLayerLoader"/>'s job.
/// <c>SqlAgent.Cli emit-grants</c> needs only this stage, so CI can regenerate the grants without a database.
/// </summary>
public static partial class SemanticConfigParser
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        // A repeated key would silently replace the first one, which can drop a deny. Fail instead.
        .WithDuplicateKeyChecking()
        .Build();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex PlainIdentifier();

    [GeneratedRegex(@"^[A-Za-z_*?][A-Za-z0-9_*?]*$")]
    private static partial Regex GlobIdentifier();

    public static SemanticConfig Load(AgentAssetsOptions assets)
    {
        var (path, content) = assets.ReadSemanticYaml();
        return Parse(content, path);
    }

    public static SemanticConfig Parse(string yaml, string sourcePath)
    {
        SemanticYamlDocument? document;
        try
        {
            document = Deserializer.Deserialize<SemanticYamlDocument?>(yaml);
        }
        catch (YamlException ex)
        {
            var reason = ex.InnerException?.Message ?? ex.Message;
            throw new SemanticLayerException(sourcePath, [$"line {ex.Start.Line}, column {ex.Start.Column}: {reason}"]);
        }

        if (document is null) throw new SemanticLayerException(sourcePath, ["the file is empty"]);

        var errors = new List<string>();
        var config = Build(document, sourcePath, errors);
        if (errors.Count > 0) throw new SemanticLayerException(sourcePath, errors);
        return config;
    }

    private static SemanticConfig Build(SemanticYamlDocument doc, string sourcePath, List<string> errors)
    {
        var roles = ParseRoles(doc.Roles, errors);
        var allTables = new HashSet<string>(roles.Values.SelectMany(r => r.Tables), StringComparer.OrdinalIgnoreCase);

        var global = ParseDenyRules(doc.GlobalDenyColumns, "global_deny_columns", errors);

        var tables = new Dictionary<string, TableMeta>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawName, meta) in doc.Tables ?? [])
        {
            var path = $"tables.{rawName}";
            var name = ParseTableName(rawName, path, errors);
            if (name is null) continue;
            if (!allTables.Contains(name)) errors.Add($"{path}: the table is not in any role's tables (stale entry?)");

            var synonyms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (term, column) in meta?.Synonyms ?? [])
            {
                if (string.IsNullOrWhiteSpace(term) || !PlainIdentifier().IsMatch(column ?? ""))
                    errors.Add($"{path}.synonyms.{term}: expected a column name");
                else synonyms[term] = column!;
            }

            if (!tables.TryAdd(name, new TableMeta(meta?.Description?.Trim(), synonyms)))
                errors.Add($"{path}: table listed twice");
        }

        var hints = new List<JoinHint>();
        foreach (var (hint, i) in (doc.JoinHints ?? []).Select((h, i) => (h, i)))
        {
            var path = $"join_hints[{i}]";
            var hintTables = (hint?.Tables ?? []).Select(t => ParseTableName(t, $"{path}.tables", errors)).OfType<string>().ToList();
            if (hintTables.Count < 2) errors.Add($"{path}.tables: list at least two tables");
            foreach (var missing in hintTables.Where(t => !allTables.Contains(t)))
                errors.Add($"{path}.tables: {missing} is not in any role's tables");
            if (string.IsNullOrWhiteSpace(hint?.Hint)) errors.Add($"{path}.hint: text is required");
            else hints.Add(new JoinHint(hintTables, hint.Hint.Trim()));
        }

        var examples = new List<SqlExample>();
        foreach (var (example, i) in (doc.Examples ?? []).Select((e, i) => (e, i)))
        {
            var path = $"examples[{i}]";
            var exampleRoles = new List<Role>();
            foreach (var key in example?.Roles ?? [])
            {
                if (RoleExtensions.TryParse(key, out var role)) exampleRoles.Add(role);
                else errors.Add($"{path}.roles: unknown role '{key}'");
            }

            if (exampleRoles.Count == 0) errors.Add($"{path}.roles: tag at least one role");
            if (string.IsNullOrWhiteSpace(example?.Question)) errors.Add($"{path}.question: text is required");
            if (string.IsNullOrWhiteSpace(example?.Sql))
            {
                errors.Add($"{path}.sql: text is required");
                continue;
            }

            foreach (var role in exampleRoles.Where(roles.ContainsKey))
            {
                var allowed = roles[role].Tables.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var problem in ExampleSqlValidator.Validate(example.Sql, allowed, deniedColumns: null))
                    errors.Add($"{path} (role {role.ToKey()}): {problem}");
            }

            examples.Add(new SqlExample(exampleRoles, example.Question?.Trim() ?? "", example.Sql.Trim()));
        }

        return new SemanticConfig(roles, global, tables, hints, examples, sourcePath);
    }

    private static Dictionary<Role, RoleConfig> ParseRoles(Dictionary<string, RoleYaml>? raw, List<string> errors)
    {
        var roles = new Dictionary<Role, RoleConfig>();
        foreach (var (key, yaml) in raw ?? [])
        {
            var path = $"roles.{key}";
            if (!RoleExtensions.TryParse(key, out var role))
            {
                errors.Add($"{path}: unknown role (expected sales_rep, finance or admin)");
                continue;
            }

            var tables = new List<string>();
            foreach (var rawTable in yaml?.Tables ?? [])
            {
                var name = ParseTableName(rawTable, $"{path}.tables", errors);
                if (name is null) continue;
                if (tables.Contains(name, StringComparer.OrdinalIgnoreCase)) errors.Add($"{path}.tables: {name} listed twice");
                else tables.Add(name);
            }

            if (tables.Count == 0) errors.Add($"{path}.tables: list at least one table");

            var deny = ParseDenyRules(yaml?.DenyColumns, $"{path}.deny_columns", errors);
            foreach (var rule in deny.Where(r => !r.IsWildcard))
            {
                // A concrete table that the role cannot read is a stale or misspelled entry.
                var table = NameNormalizer.Table(rule.SchemaPattern, rule.TablePattern);
                if (!tables.Contains(table, StringComparer.OrdinalIgnoreCase))
                    errors.Add($"{path}.deny_columns.{table}: the table is not in this role's tables");
            }

            roles[role] = new RoleConfig(role, tables, deny);
        }

        foreach (var missing in RoleExtensions.All.Where(r => !roles.ContainsKey(r)))
            errors.Add($"roles.{missing.ToKey()}: the role is missing (all three roles are required)");

        return roles;
    }

    private static List<DenyRule> ParseDenyRules(Dictionary<string, List<string>>? raw, string path, List<string> errors)
    {
        var rules = new List<DenyRule>();
        foreach (var (key, columns) in raw ?? [])
        {
            var parts = NameNormalizer.SplitParts(key);
            if (parts.Count != 2 || parts.Any(p => !GlobIdentifier().IsMatch(p)))
            {
                errors.Add($"{path}.{key}: expected 'Schema.Table' (a '*' or '?' pattern is allowed)");
                continue;
            }

            var patterns = (columns ?? []).Select(c => c?.Trim() ?? "").ToList();
            if (patterns.Count == 0 || patterns.Any(c => !GlobIdentifier().IsMatch(c)))
            {
                errors.Add($"{path}.{key}: list column names (a '*' or '?' pattern is allowed)");
                continue;
            }

            rules.Add(new DenyRule(parts[0], parts[1], patterns));
        }

        return rules;
    }

    private static string? ParseTableName(string? raw, string path, List<string> errors)
    {
        var parts = NameNormalizer.SplitParts(raw ?? "");
        if (parts.Count != 2 || parts.Any(p => !PlainIdentifier().IsMatch(p)))
        {
            errors.Add($"{path}: '{raw}' is not a Schema.Table name");
            return null;
        }

        return NameNormalizer.Table(parts[0], parts[1]);
    }
}
