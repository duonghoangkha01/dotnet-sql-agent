namespace SqlAgent.Core.Schema;

// The raw shape of semantic.yaml, as YamlDotNet fills it in (snake_case keys). Unknown keys are an error,
// so a typo such as "deny_column" fails at startup instead of silently allowing more than intended.
// SemanticConfigParser validates these and turns them into the immutable SemanticConfig.

internal sealed class SemanticYamlDocument
{
    public Dictionary<string, RoleYaml>? Roles { get; set; }
    public Dictionary<string, List<string>>? GlobalDenyColumns { get; set; }
    public Dictionary<string, TableYaml>? Tables { get; set; }
    public List<JoinHintYaml>? JoinHints { get; set; }
    public List<ExampleYaml>? Examples { get; set; }
}

internal sealed class RoleYaml
{
    public List<string>? Tables { get; set; }
    public Dictionary<string, List<string>>? DenyColumns { get; set; }
}

internal sealed class TableYaml
{
    public string? Description { get; set; }

    /// <summary>Business term to column, for example <c>revenue: TotalDue</c>.</summary>
    public Dictionary<string, string>? Synonyms { get; set; }
}

internal sealed class JoinHintYaml
{
    public List<string>? Tables { get; set; }
    public string? Hint { get; set; }
}

internal sealed class ExampleYaml
{
    public List<string>? Roles { get; set; }
    public string? Question { get; set; }
    public string? Sql { get; set; }
}
