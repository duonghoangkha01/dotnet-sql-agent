using System.Text.RegularExpressions;
using SqlAgent.Core.Schema;

namespace SqlAgent.Core.Tests.Schema;

public partial class GrantScriptGeneratorTests
{
    private static string GoldenPath(string file) => Path.Combine(AppContext.BaseDirectory, "Schema", "Grants", "Golden", file);

    private static string GenerateFor(string yaml) => GrantScriptGenerator.Generate(SemanticConfigParser.Parse(yaml, "test.yaml"));

    [GeneratedRegex(@"^GRANT SELECT ON \[(?<schema>\w+)\]\.\[(?<table>\w+)\] TO \[sqlagent_(?<role>\w+)\];$", RegexOptions.Multiline)]
    private static partial Regex GrantLine();

    [Fact]
    public void The_sample_layer_matches_the_golden_script()
    {
        var yaml = File.ReadAllText(GoldenPath("grants-sample.yaml"));
        var expected = File.ReadAllText(GoldenPath("grants-sample.sql")).ReplaceLineEndings("\n");

        Assert.Equal(expected, GenerateFor(yaml));
    }

    [Fact]
    public void The_output_is_deterministic_and_uses_LF_only()
    {
        var yaml = File.ReadAllText(GoldenPath("grants-sample.yaml"));

        var first = GenerateFor(yaml);

        Assert.Equal(first, GenerateFor(yaml));
        Assert.DoesNotContain('\r', first);
    }

    [Fact]
    public void The_order_of_tables_in_the_yaml_does_not_change_the_output()
    {
        var yaml = File.ReadAllText(GoldenPath("grants-sample.yaml"));
        var reordered = yaml.Replace("[Sales.SalesOrderHeader, Sales.SalesTerritory, Person.Person]", "[Person.Person, Sales.SalesTerritory, Sales.SalesOrderHeader]");

        Assert.NotEqual(yaml, reordered);
        Assert.Equal(GenerateFor(yaml), GenerateFor(reordered));
    }

    [Fact]
    public void The_script_keeps_the_reset_preamble_and_runs_in_one_transaction()
    {
        var script = GenerateFor(File.ReadAllText(GoldenPath("grants-sample.yaml")));

        Assert.Contains("SET XACT_ABORT ON;", script);
        Assert.Contains("BEGIN TRANSACTION;", script);
        Assert.Contains("REVOKE ' + p.permission_name", script);
        Assert.Contains("DROP MEMBER", script);
        Assert.Contains("COMMIT TRANSACTION;", script);
        Assert.Contains("GRANT VIEW DEFINITION TO sqlagent_app;", script);
        Assert.True(script.IndexOf("REVOKE", StringComparison.Ordinal) < script.IndexOf("GRANT SELECT", StringComparison.Ordinal),
            "the reset must come before the grants");
    }

    [Fact]
    public void Concrete_denies_apply_only_to_tables_the_role_can_read()
    {
        var script = GenerateFor(File.ReadAllText(GoldenPath("grants-sample.yaml")));

        Assert.Contains("DENY SELECT ON [Sales].[CreditCard] ([CardNumber]) TO [sqlagent_finance];", script);
        Assert.Contains("DENY SELECT ON [Sales].[CreditCard] ([CardNumber]) TO [sqlagent_admin];", script);
        Assert.DoesNotContain("CreditCard] ([CardNumber]) TO [sqlagent_sales_rep]", script);
        Assert.DoesNotContain("NationalIDNumber]) TO [sqlagent_finance]", script);
    }

    [Fact]
    public void Patterns_become_an_expansion_block_with_like_escaping()
    {
        var script = GenerateFor(File.ReadAllText(GoldenPath("grants-sample.yaml")));

        Assert.Contains("(N'sqlagent_sales_rep', N'Person', N'%', N'Password%')", script);
        Assert.Contains("(N'sqlagent_sales_rep', N'Person', N'Person', N'Info%')", script);
        Assert.Contains("ESCAPE N'\\'", script);
        Assert.Contains("COLLATE Latin1_General_100_CI_AS LIKE", script);
    }

    [Fact]
    public void A_layer_with_no_patterns_has_no_expansion_block()
    {
        var yaml = File.ReadAllText(GoldenPath("grants-sample.yaml"))
            .Replace("  Person.*: [\"Password*\"]", "")
            .Replace("\"Info*\"", "\"Notes\"");

        Assert.DoesNotContain("@wild", GenerateFor(yaml));
    }

    [Fact]
    public void The_shipped_layer_grants_exactly_the_tables_of_each_role()
    {
        var config = SemanticConfigParser.Load(new AgentAssetsOptions());
        var script = GrantScriptGenerator.Generate(config);

        var granted = GrantLine().Matches(script)
            .GroupBy(m => m.Groups["role"].Value, m => $"{m.Groups["schema"].Value}.{m.Groups["table"].Value}")
            .ToDictionary(g => g.Key, g => g.Order(StringComparer.OrdinalIgnoreCase).ToList());

        foreach (var role in RoleExtensions.All)
        {
            var expected = config.Roles[role].Tables.Order(StringComparer.OrdinalIgnoreCase).ToList();
            Assert.Equal(expected, granted[role.ToKey()]);
        }
    }

    [Fact]
    public void The_shipped_layer_never_grants_a_table_outside_the_role()
    {
        var config = SemanticConfigParser.Load(new AgentAssetsOptions());
        var script = GrantScriptGenerator.Generate(config);

        Assert.DoesNotContain("TO [sqlagent_sales_rep]", string.Join('\n', script.Split('\n').Where(l => l.Contains("HumanResources"))));
        Assert.DoesNotContain("[Person].[Password]", string.Join('\n', script.Split('\n').Where(l => l.StartsWith("GRANT SELECT ON"))));
    }
}
