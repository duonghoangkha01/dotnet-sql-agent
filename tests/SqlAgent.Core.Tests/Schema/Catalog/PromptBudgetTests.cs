using SqlAgent.Core.Schema;

namespace SqlAgent.Core.Tests.Schema;

public class PromptBudgetTests
{
    private static readonly SchemaCatalog Catalog = SchemaTestData.Catalog();

    [Fact]
    public void A_small_prompt_fits_the_default_context()
    {
        PromptBudget.Validate(Catalog, new string('x', 400), PromptBudget.DefaultContextWindowTokens, "test");
    }

    [Fact]
    public void A_fixed_prompt_over_half_the_context_fails_for_each_role_that_exceeds_it()
    {
        // 8192 * 0.5 = 4096 tokens = about 16k characters.
        var ex = Assert.Throws<SemanticLayerException>(() =>
            PromptBudget.Validate(Catalog, new string('x', 17_000), PromptBudget.DefaultContextWindowTokens, "test/prompts"));

        Assert.StartsWith("test/prompts", ex.Message);
        Assert.Equal(3, ex.Errors.Count);
        Assert.Contains("4096", ex.Errors[0]);
    }

    [Fact]
    public void A_smaller_context_window_tightens_the_budget()
    {
        var text = new string('x', 6_000); // about 1500 tokens: fine for 8k, too much for 2k.

        PromptBudget.Validate(Catalog, text, 8192, "test");
        Assert.Throws<SemanticLayerException>(() => PromptBudget.Validate(Catalog, text, 2048, "test"));
    }

    [Fact]
    public void Estimate_counts_the_list_of_tables_and_the_roles_examples()
    {
        var estimate = PromptBudget.Estimate(Catalog, "");

        // Admin reads more tables than sales_rep, and finance has an extra example.
        Assert.True(estimate[Role.Admin] > estimate[Role.SalesRep]);
        Assert.True(estimate[Role.Finance] > 0);
    }

    [Fact]
    public void ListTables_of_the_shipped_layer_stays_compact_for_every_role()
    {
        var config = SemanticConfigParser.Load(new AgentAssetsOptions());

        foreach (var role in RoleExtensions.All)
        {
            var summaries = config.Roles[role].Tables.Select(t => new TableSummary(t, config.Tables.GetValueOrDefault(t)?.Description)).ToList();

            Assert.All(summaries, s => Assert.False(string.IsNullOrWhiteSpace(s.Description), $"{role.ToKey()}: {s.Name} has no description"));
            var tokens = PromptBudget.EstimateTokens(PromptBudget.RenderListTables(summaries));
            Assert.True(tokens < 1500, $"{role.ToKey()}: ListTables is about {tokens} tokens");
        }
    }
}
