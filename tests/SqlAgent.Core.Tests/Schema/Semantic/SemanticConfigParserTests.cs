using SqlAgent.Core.Schema;

namespace SqlAgent.Core.Tests.Schema;

public class SemanticConfigParserTests
{
    private static SemanticLayerException Fails(string yaml) =>
        Assert.Throws<SemanticLayerException>(() => SchemaTestData.Config(yaml));

    /// <summary>The test YAML with one substring replaced, to introduce a single mistake.</summary>
    private static string With(string from, string to)
    {
        Assert.Contains(from, SchemaTestData.Yaml);
        return SchemaTestData.Yaml.Replace(from, to);
    }

    [Fact]
    public void The_shipped_semantic_yaml_is_valid_offline()
    {
        var config = SemanticConfigParser.Load(new AgentAssetsOptions());

        Assert.Equal(AgentAssetsOptions.EmbeddedSemanticYamlPath, config.SourcePath);
        Assert.Equal(3, config.Roles.Count);
        Assert.Equal(8, config.Examples.Count);
    }

    [Fact]
    public void Roles_and_tables_are_normalized()
    {
        var config = SchemaTestData.Config();

        Assert.Contains("Sales.SalesTerritory", config.Roles[Role.Admin].Tables);
        Assert.DoesNotContain("[Sales].[SalesTerritory]", config.Roles[Role.Admin].Tables);
    }

    [Fact]
    public void A_misspelled_key_fails_with_the_file_and_the_position()
    {
        var ex = Fails(With("deny_columns:", "deny_column:"));

        Assert.StartsWith(SchemaTestData.SourcePath, ex.Message);
        Assert.Matches(@"line \d+, column \d+", ex.Message);
        Assert.Contains("deny_column", ex.Message);
    }

    [Fact]
    public void A_missing_role_is_reported()
    {
        var yaml = With("  admin:\n    tables:", "  administrator:\n    tables:");

        var ex = Fails(yaml);

        Assert.Contains(ex.Errors, e => e.StartsWith("roles.administrator") && e.Contains("unknown role"));
        Assert.Contains(ex.Errors, e => e.StartsWith("roles.admin") && e.Contains("missing"));
    }

    [Fact]
    public void An_explicit_deny_on_a_table_the_role_cannot_read_is_reported()
    {
        var ex = Fails(With("Person.Person: [Demographics]", "Sales.CreditCard: [CardNumber]"));

        Assert.Contains(ex.Errors, e => e.StartsWith("roles.sales_rep.deny_columns.Sales.CreditCard"));
    }

    [Fact]
    public void An_example_that_reads_a_table_outside_a_tagged_role_is_reported_per_role()
    {
        var yaml = With("sql: SELECT Name, SalesYTD FROM Sales.SalesTerritory",
            "sql: SELECT CardType FROM Sales.CreditCard");

        var ex = Fails(yaml.Replace("roles: [finance, admin]\n    question: Territory", "roles: [sales_rep, finance]\n    question: Territory"));

        Assert.Contains(ex.Errors, e => e.StartsWith("examples[1] (role sales_rep)") && e.Contains("Sales.CreditCard"));
        Assert.DoesNotContain(ex.Errors, e => e.Contains("(role finance)"));
    }

    [Theory]
    [InlineData("SELECT * FROM Sales.SalesOrderHeader", "SELECT *")]
    [InlineData("SELECT TotalDue FROM SalesOrderHeader", "2-part")]
    [InlineData("SELECT TotalDue FROM Sales.SalesOrderHeader; DELETE Sales.SalesOrderHeader", "exactly one SELECT")]
    [InlineData("SELEC TotalDue FROM Sales.SalesOrderHeader", "does not parse")]
    public void An_example_must_be_one_parsable_select_with_explicit_columns(string sql, string expectedFragment)
    {
        var yaml = With("sql: SELECT Name, SalesYTD FROM Sales.SalesTerritory", $"sql: \"{sql}\"");

        var ex = Fails(yaml);

        Assert.Contains(ex.Errors, e => e.StartsWith("examples[1]") && e.Contains(expectedFragment));
    }

    [Fact]
    public void A_cte_name_is_not_mistaken_for_a_table()
    {
        var yaml = With("sql: SELECT Name, SalesYTD FROM Sales.SalesTerritory",
            "sql: WITH t AS (SELECT TerritoryID FROM Sales.SalesTerritory) SELECT TerritoryID FROM t");

        var config = SchemaTestData.Config(yaml);

        Assert.Equal(2, config.Examples.Count);
    }

    [Fact]
    public void Every_problem_is_reported_in_one_error()
    {
        var yaml = With("Person.Person: [Demographics]", "Sales.CreditCard: [CardNumber]")
            .Replace("tables: [Sales.SalesOrderHeader, Sales.SalesTerritory, Sales.CreditCard]", "tables: [Sales.SalesOrderHeader, Sales.SalesOrderHeader]");

        var ex = Fails(yaml);

        Assert.True(ex.Errors.Count >= 2, string.Join("; ", ex.Errors));
    }

    [Theory]
    [InlineData("SELECT TotalDue FROM dbo.ufnGetSalesFor(1)")]
    [InlineData("SELECT dbo.ufnFormat(TotalDue) FROM Sales.SalesOrderHeader")]
    [InlineData("SELECT TotalDue INTO #copy FROM Sales.SalesOrderHeader")]
    [InlineData("SELECT a FROM OPENROWSET('SQLNCLI', 'x', 'SELECT 1') AS r")]
    public void An_example_may_not_use_functions_openrowset_or_select_into(string sql)
    {
        var yaml = With("sql: SELECT Name, SalesYTD FROM Sales.SalesTerritory", $"sql: \"{sql}\"");

        var ex = Fails(yaml);

        Assert.Contains(ex.Errors, e => e.StartsWith("examples[1]") && (e.Contains("only plain queries") || e.Contains("does not parse")));
    }

    [Fact]
    public void A_repeated_key_is_an_error_instead_of_silently_replacing_the_first()
    {
        // The second Sales.SalesTerritory entry would otherwise overwrite the first and drop two denied columns.
        var yaml = With("Sales.SalesTerritory: [SalesYTD, SalesLastYear]",
            "Sales.SalesTerritory: [SalesYTD, SalesLastYear]\n      Sales.SalesTerritory: [CostYTD]");

        var ex = Fails(yaml);

        Assert.Matches(@"line \d+", ex.Message);
        Assert.Contains("Sales.SalesTerritory", ex.Message);
    }

    [Fact]
    public void A_repeated_role_is_an_error()
    {
        var yaml = SchemaTestData.Yaml.Replace("  finance:\n", "  sales_rep:\n    tables: [Sales.SalesOrderHeader]\n  finance:\n");

        Assert.Contains("sales_rep", Fails(yaml).Message);
    }

    [Fact]
    public void An_empty_file_is_an_error() => Assert.Contains("empty", Fails("").Message);

    [Theory]
    [InlineData("Sales.SalesOrderHeader; DROP TABLE x")]
    [InlineData("Sales.Order'Header")]
    [InlineData("SalesOrderHeader")]
    [InlineData("a.b.c")]
    public void A_table_name_must_be_a_plain_schema_dot_table(string name)
    {
        var ex = Fails(With("tables: [Sales.SalesOrderHeader, Sales.SalesTerritory, Sales.CreditCard]", $"tables: [\"{name}\"]"));

        Assert.Contains(ex.Errors, e => e.StartsWith("roles.finance.tables"));
    }
}
