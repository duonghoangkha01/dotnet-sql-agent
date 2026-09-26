using SqlAgent.Core.Schema;

namespace SqlAgent.Core.Tests.Schema;

public class SchemaCatalogTests
{
    private static readonly SchemaCatalog Catalog = SchemaTestData.Catalog();

    private static string[] Names(Role role) => Catalog.ListTables(role).Select(t => t.Name).ToArray();

    [Fact]
    public void ListTables_is_filtered_per_role_and_sorted()
    {
        Assert.Equal(["Person.Person", "Sales.SalesOrderHeader", "Sales.SalesTerritory"], Names(Role.SalesRep));
        Assert.Equal(["Sales.CreditCard", "Sales.SalesOrderHeader", "Sales.SalesTerritory"], Names(Role.Finance));
        Assert.Equal(6, Names(Role.Admin).Length);
    }

    [Fact]
    public void ListTables_carries_the_curated_one_line_description()
    {
        var header = Catalog.ListTables(Role.SalesRep).Single(t => t.Name == "Sales.SalesOrderHeader");

        Assert.Equal("One row per order.", header.Description);
    }

    [Fact]
    public void A_sales_rep_never_sees_tables_outside_the_role()
    {
        var everything = Catalog.DescribeTables(Role.SalesRep,
            ["HumanResources.Employee", "Sales.CreditCard", "Person.Password"]);

        Assert.Empty(everything.Tables);
        Assert.DoesNotContain(Names(Role.SalesRep), n => n.StartsWith("HumanResources"));
    }

    [Fact]
    public void AllowList_tables_are_normalized_and_case_insensitive()
    {
        var admin = Catalog.GetAllowList(Role.Admin);

        // The YAML wrote "[Sales].[SalesTerritory]".
        Assert.Contains("Sales.SalesTerritory", admin.Tables);
        Assert.Contains("SALES.salesterritory", admin.Tables);
        Assert.DoesNotContain("Sales.Nothing", admin.Tables);
    }

    [Fact]
    public void Role_deny_columns_are_kept_and_looked_up_case_insensitively()
    {
        var allow = Catalog.GetAllowList(Role.SalesRep);

        Assert.Equal(["SalesLastYear", "SalesYTD"], allow.DeniedColumns["sales.salesterritory"].Order());
        Assert.Contains("DEMOGRAPHICS", allow.DeniedColumns["Person.Person"]);
    }

    [Fact]
    public void Global_deny_columns_reach_every_role_that_can_read_the_table()
    {
        Assert.Equal(["CardNumber"], Catalog.GetAllowList(Role.Finance).DeniedColumns["Sales.CreditCard"]);
        Assert.Equal(["CardNumber"], Catalog.GetAllowList(Role.Admin).DeniedColumns["Sales.CreditCard"]);
        Assert.Equal(["NationalIDNumber"], Catalog.GetAllowList(Role.Admin).DeniedColumns["HumanResources.Employee"]);
        // A role without the table has nothing to deny there.
        Assert.DoesNotContain("Sales.CreditCard", Catalog.GetAllowList(Role.SalesRep).DeniedColumns.Keys);
    }

    [Fact]
    public void Globs_are_expanded_against_the_real_columns_at_load()
    {
        var admin = Catalog.GetAllowList(Role.Admin);

        // "Person.*" with "Password*": only Person.Password has such columns.
        Assert.Equal(["PasswordHash", "PasswordSalt"], admin.DeniedColumns["Person.Password"].Order());
        Assert.DoesNotContain("Person.Person", admin.DeniedColumns.Keys);
    }

    [Fact]
    public void DescribeTables_removes_denied_columns_and_what_depends_on_them()
    {
        var territory = Catalog.DescribeTables(Role.SalesRep, ["Sales.SalesTerritory"]).Tables.Single();

        Assert.Equal(["TerritoryID", "Name"], territory.Columns.Select(c => c.Name));
        Assert.Equal(["region"], territory.Synonyms.Keys);
        // Finance may read every column of the same table.
        Assert.Equal(4, Catalog.DescribeTables(Role.Finance, ["Sales.SalesTerritory"]).Tables.Single().Columns.Count);
    }

    [Fact]
    public void Foreign_keys_and_join_hints_appear_only_when_the_role_can_see_both_ends()
    {
        var forSalesRep = Catalog.DescribeTables(Role.SalesRep, ["Sales.SalesOrderHeader"]).Tables.Single();
        var forFinance = Catalog.DescribeTables(Role.Finance, ["Sales.SalesOrderHeader"]).Tables.Single();

        Assert.Equal(["Sales.SalesTerritory"], forSalesRep.ForeignKeys.Select(f => f.ReferencedTable));
        Assert.Equal(["Join on TerritoryID."], forSalesRep.JoinHints);
        Assert.Equal(2, forFinance.ForeignKeys.Count);
        Assert.Equal(["Join on TerritoryID.", "Card details are finance-only."], forFinance.JoinHints);
    }

    [Fact]
    public void Unknown_and_denied_tables_get_the_same_message()
    {
        var denied = Catalog.DescribeTables(Role.SalesRep, ["HumanResources.Employee"]);
        var unknown = Catalog.DescribeTables(Role.SalesRep, ["Nope.Nothing"]);

        Assert.Equal(unknown.Errors.Single().Replace("Nope.Nothing", "X"), denied.Errors.Single().Replace("HumanResources.Employee", "X"));
        Assert.DoesNotContain("permission", denied.Errors.Single(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role", denied.Errors.Single(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_null_or_blank_name_is_just_not_available()
    {
        var result = Catalog.DescribeTables(Role.SalesRep, [null!, "", "  "]);

        Assert.Empty(result.Tables);
        Assert.Equal(3, result.Errors.Count);
    }

    [Fact]
    public void DescribeTables_accepts_bracketed_names_and_ignores_case_and_duplicates()
    {
        var result = Catalog.DescribeTables(Role.SalesRep, ["[sales].[SALESTERRITORY]", "Sales.SalesTerritory", "Person.Person"]);

        Assert.Equal(["Sales.SalesTerritory", "Person.Person"], result.Tables.Select(t => t.Name));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Examples_are_filtered_per_role()
    {
        Assert.Equal(["Orders per territory?"], Catalog.GetExamples(Role.SalesRep).Select(e => e.Question));
        Assert.Equal(2, Catalog.GetExamples(Role.Finance).Count);
        Assert.Equal(2, Catalog.GetExamples(Role.Admin).Count);
    }

    [Fact]
    public void The_catalog_cannot_be_changed_through_what_it_returns()
    {
        var allow = Catalog.GetAllowList(Role.SalesRep);

        Assert.Throws<NotSupportedException>(() => ((ISet<string>)allow.Tables).Add("HumanResources.Employee"));
        Assert.Throws<NotSupportedException>(() => ((ISet<string>)allow.DeniedColumns["Sales.SalesTerritory"]).Remove("SalesYTD"));
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, IReadOnlySet<string>>)allow.DeniedColumns).Remove("Sales.SalesTerritory"));
        Assert.Throws<NotSupportedException>(() => ((IList<TableSummary>)Catalog.ListTables(Role.SalesRep)).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ColumnDescription>)Catalog.DescribeTables(Role.SalesRep, ["Sales.SalesTerritory"]).Tables[0].Columns).Clear());
    }
}
