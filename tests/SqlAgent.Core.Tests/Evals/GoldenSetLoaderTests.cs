using SqlAgent.Cli.Evals;

namespace SqlAgent.Core.Tests.Evals;

public class GoldenSetLoaderTests
{
    private static string Item(string id = "a", string persona = "demo-admin", string tier = "simple", string sql = "SELECT 1") =>
        $$"""{ "id": "{{id}}", "persona": "{{persona}}", "question": "Q?", "referenceSql": "{{sql}}", "tier": "{{tier}}", "ordered": false, "holdout": false }""";

    private static string RepositoryGoldenSet()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SqlAgent.sln"))) return Path.Combine(dir.FullName, "evals", "golden.json");
        }

        throw new InvalidOperationException("SqlAgent.sln was not found.");
    }

    [Fact]
    public void A_valid_set_loads_with_every_field()
    {
        var items = GoldenSetLoader.Parse($"[{Item("a")}, {Item("b", "demo-sales-rep-nw", "multi-join")}]");

        Assert.Equal(["a", "b"], items.Select(i => i.Id));
        Assert.Equal(Role.SalesRep, items[1].User.Role);
        Assert.Equal(1, items[1].User.TerritoryId);
        Assert.Equal(Role.Admin, items[0].User.Role);
    }

    [Fact]
    public void Every_problem_is_listed_at_once()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GoldenSetLoader.Parse(
            $"[{Item("a")}, {Item("a")}, {Item("c", persona: "somebody")}, {Item("d", tier: "easy")}, {Item("e", sql: " ")}]"));

        Assert.Contains("duplicate id", ex.Message);
        Assert.Contains("persona must be one of", ex.Message);
        Assert.Contains("tier must be one of", ex.Message);
        Assert.Contains("referenceSql is required", ex.Message);
    }

    [Fact]
    public void An_unknown_field_is_an_error_so_a_typo_cannot_silently_change_scoring()
    {
        Assert.Throws<InvalidOperationException>(() => GoldenSetLoader.Parse(
            """[{ "id": "a", "persona": "demo-admin", "question": "Q", "referenceSql": "SELECT 1", "tier": "simple", "order": true }]"""));
    }

    [Fact]
    public void An_empty_or_malformed_file_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => GoldenSetLoader.Parse("[]"));
        Assert.Throws<InvalidOperationException>(() => GoldenSetLoader.Parse("{ not json"));
    }

    [Fact]
    public void The_shipped_golden_set_meets_the_published_shape()
    {
        var items = GoldenSetLoader.LoadFile(RepositoryGoldenSet());

        Assert.True(items.Count >= 30, $"{items.Count} items");
        Assert.True(items.Count(i => i.Tier == GoldenSetLoader.SimpleTier) >= 15);
        Assert.True(items.Count(i => i.Tier == GoldenSetLoader.MultiJoinTier) >= 10);
        Assert.True(items.Count(i => i.Tier == GoldenSetLoader.TimeWindowTier) >= 5);
        Assert.Contains(items, i => i.Holdout);
        Assert.Equal(Role.SalesRep, items.First(i => i.Persona == "demo-sales-rep-nw").User.Role);
        // Every persona is exercised, so row-level security and the column denials show up in the accuracy.
        Assert.Equal(3, items.Select(i => i.Persona).Distinct().Count());
    }

    [Fact]
    public void Reference_queries_never_ask_for_data_through_a_wildcard_or_a_denied_name()
    {
        var items = GoldenSetLoader.LoadFile(RepositoryGoldenSet());

        // The database run (integration tests) is the real proof; this catches an obvious slip without a database.
        Assert.All(items, i => Assert.DoesNotContain("SELECT *", i.ReferenceSql, StringComparison.OrdinalIgnoreCase));
        Assert.All(items, i => Assert.DoesNotContain("CardNumber", i.ReferenceSql, StringComparison.OrdinalIgnoreCase));
        Assert.All(items, i => Assert.DoesNotContain("NationalIDNumber", i.ReferenceSql, StringComparison.OrdinalIgnoreCase));
    }
}
