using SqlAgent.Core.Schema;

namespace SqlAgent.Core.Tests.Schema;

/// <summary>Startup validation: every mistake in semantic.yaml, checked against the schema, fails with its YAML path.</summary>
public class SemanticLayerLoaderTests
{
    private static SemanticLayerException Fails(string from, string to)
    {
        Assert.Contains(from, SchemaTestData.Yaml);
        return Assert.Throws<SemanticLayerException>(() => SchemaTestData.Catalog(SchemaTestData.Yaml.Replace(from, to)));
    }

    [Fact]
    public void A_correct_file_loads() => Assert.NotNull(SchemaTestData.Catalog());

    [Fact]
    public void A_misspelled_denied_column_names_its_yaml_path_and_the_file()
    {
        var ex = Fails("Sales.SalesTerritory: [SalesYTD, SalesLastYear]", "Sales.SalesTerritory: [SalesYTD, SalesLastYeer]");

        Assert.StartsWith(SchemaTestData.SourcePath, ex.Message);
        Assert.Contains(ex.Errors, e => e.StartsWith("roles.sales_rep.deny_columns.Sales.SalesTerritory") && e.Contains("SalesLastYeer"));
    }

    [Fact]
    public void A_table_missing_from_the_database_is_reported()
    {
        var ex = Fails("HumanResources.Employee, Person.Person, Person.Password]",
            "HumanResources.Employees, Person.Person, Person.Password]");

        Assert.Contains(ex.Errors, e => e.StartsWith("roles.admin.tables") && e.Contains("HumanResources.Employees"));
    }

    [Fact]
    public void A_glob_that_matches_nothing_is_reported()
    {
        var ex = Fails("Person.*: [\"Password*\"]", "Person.*: [\"Pwd*\"]");

        Assert.Contains(ex.Errors, e => e.StartsWith("global_deny_columns.Person.*") && e.Contains("Pwd*"));
    }

    [Fact]
    public void A_deny_entry_for_a_table_that_does_not_exist_is_reported()
    {
        var ex = Fails("Sales.CreditCard: [CardNumber]", "Sales.CreditCards: [CardNumber]");

        Assert.Contains(ex.Errors, e => e.StartsWith("global_deny_columns.Sales.CreditCards") && e.Contains("no table matches"));
    }

    [Fact]
    public void A_synonym_for_a_missing_column_is_reported()
    {
        var ex = Fails("synonyms: { revenue: TotalDue }", "synonyms: { revenue: TotalDues }");

        Assert.Contains(ex.Errors, e => e.StartsWith("tables.Sales.SalesOrderHeader.synonyms.revenue"));
    }

    [Fact]
    public void An_example_that_names_a_column_denied_to_its_role_is_reported()
    {
        var ex = Fails("SELECT t.Name, COUNT(*) AS Orders", "SELECT t.SalesYTD, COUNT(*) AS Orders");

        Assert.Contains(ex.Errors, e => e.StartsWith("examples[0] (role sales_rep)") && e.Contains("SalesYTD"));
        // Finance and admin may read that column, so their tags are fine.
        Assert.DoesNotContain(ex.Errors, e => e.Contains("(role finance)") || e.Contains("(role admin)"));
    }

    [Fact]
    public void All_problems_are_reported_together()
    {
        var yaml = SchemaTestData.Yaml
            .Replace("[SalesYTD, SalesLastYear]", "[SalesYTD, SalesLastYeer]")
            .Replace("synonyms: { revenue: TotalDue }", "synonyms: { revenue: TotalDues }");

        var ex = Assert.Throws<SemanticLayerException>(() => SchemaTestData.Catalog(yaml));

        Assert.True(ex.Errors.Count >= 2, string.Join("; ", ex.Errors));
    }
}
