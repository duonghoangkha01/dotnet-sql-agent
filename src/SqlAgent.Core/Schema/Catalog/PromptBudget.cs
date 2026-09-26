namespace SqlAgent.Core.Schema;

/// <summary>
/// Startup check that the fixed part of every role's prompt leaves the model room to work: system prompt, tool
/// schemas, the role's few-shot examples and its <c>ListTables</c> reply must fit in half the context window
/// (about 4k tokens of the 8k default on the dev GPU). Token counts are estimated at 4 characters per token,
/// which is close enough for a budget guard and needs no tokenizer.
/// </summary>
public static class PromptBudget
{
    public const int DefaultContextWindowTokens = 8192;

    /// <summary>The fixed prompt may use at most this fraction of the context window.</summary>
    public const double MaxFraction = 0.5;

    public static int EstimateTokens(string text) => (text.Length + 3) / 4;

    /// <summary>The compact text the model sees for <c>ListTables</c>: one line per table.</summary>
    public static string RenderListTables(IEnumerable<TableSummary> tables) =>
        string.Join('\n', tables.Select(t => t.Description is null ? t.Name : $"{t.Name}: {t.Description}"));

    /// <summary>Estimated fixed-prompt tokens per role.</summary>
    /// <param name="fixedPromptText">System prompt plus serialized tool schemas, without role-specific parts.</param>
    public static IReadOnlyDictionary<Role, int> Estimate(ISchemaCatalog catalog, string fixedPromptText)
    {
        var fixedTokens = EstimateTokens(fixedPromptText);
        return RoleExtensions.All.ToDictionary(role => role, role =>
        {
            var examples = string.Concat(catalog.GetExamples(role).Select(e => e.Question + "\n" + e.Sql + "\n"));
            return fixedTokens
                   + EstimateTokens(RenderListTables(catalog.ListTables(role)))
                   + EstimateTokens(examples);
        });
    }

    /// <exception cref="SemanticLayerException">A role's fixed prompt exceeds the budget.</exception>
    public static void Validate(
        ISchemaCatalog catalog,
        string fixedPromptText,
        int contextWindowTokens,
        string sourcePath)
    {
        var limit = (int)(contextWindowTokens * MaxFraction);
        var errors = Estimate(catalog, fixedPromptText)
            .Where(e => e.Value > limit)
            .Select(e => $"role {e.Key.ToKey()}: the fixed prompt is about {e.Value} tokens, over the budget of {limit} " +
                         $"({MaxFraction:P0} of a {contextWindowTokens}-token context). Shorten table descriptions or examples, " +
                         "or raise the model's context window.")
            .ToList();
        if (errors.Count > 0) throw new SemanticLayerException(sourcePath, errors);
    }
}
