using SqlAgent.Core;
using SqlAgent.Core.Schema;

namespace SqlAgent.IntegrationTests;

public class SchemaIntrospectorTests
{
    private static async Task<DatabaseSchema> IntrospectAsync() =>
        await new SchemaIntrospector().IntrospectAsync(LocalDbFactAttribute.ConnectionString!);

    private static TableInfo Table(DatabaseSchema schema, string name)
    {
        Assert.True(schema.TryGetTable(name, out var table), $"{name} was not introspected");
        return table;
    }

    [LocalDbFact]
    public async Task Introspection_returns_base_tables_only()
    {
        var schema = await IntrospectAsync();

        Assert.True(schema.TryGetTable("Sales.SalesOrderHeader", out _));
        Assert.False(schema.TryGetTable("Sales.vSalesPerson", out _), "views must not be introspected");
    }

    [LocalDbFact]
    public async Task Foreign_keys_are_found_including_composite_ones()
    {
        var detail = Table(await IntrospectAsync(), "Sales.SalesOrderDetail");

        var toHeader = detail.ForeignKeys.Single(f => f.ReferencedTable == "Sales.SalesOrderHeader");
        Assert.Equal(["SalesOrderID"], toHeader.Columns);
        Assert.Equal(["SalesOrderID"], toHeader.ReferencedColumns);

        var toOffer = detail.ForeignKeys.Single(f => f.ReferencedTable == "Sales.SpecialOfferProduct");
        Assert.Equal(["SpecialOfferID", "ProductID"], toOffer.Columns);
        Assert.Equal(["SpecialOfferID", "ProductID"], toOffer.ReferencedColumns);
    }

    [LocalDbFact]
    public async Task Columns_carry_readable_types_nullability_and_descriptions()
    {
        var schema = await IntrospectAsync();
        var product = Table(schema, "Production.Product");

        // "Name" is a user-defined alias type; the model needs the base type.
        Assert.Equal("nvarchar(50)", product.Columns.Single(c => c.Name == "Name").DataType);
        Assert.Equal("money", product.Columns.Single(c => c.Name == "ListPrice").DataType);
        Assert.True(product.Columns.Single(c => c.Name == "Color").IsNullable);
        Assert.False(product.Columns.Single(c => c.Name == "ProductID").IsNullable);
        Assert.Equal("decimal(8,2)", product.Columns.Single(c => c.Name == "Weight").DataType);
        Assert.False(string.IsNullOrWhiteSpace(Table(schema, "Sales.SalesOrderHeader").Description), "MS_Description of the table");
        Assert.All(product.Columns, c => Assert.False(string.IsNullOrEmpty(c.DataType)));
    }

    [LocalDbFact]
    public async Task The_shipped_semantic_yaml_is_valid_against_the_real_schema()
    {
        var schema = await IntrospectAsync();
        var config = SemanticConfigParser.Load(new AgentAssetsOptions());

        var catalog = SemanticLayerLoader.Build(config, schema);

        // sales_rep: no HR, no denied columns, no password table, and only its own examples.
        var salesRep = catalog.GetAllowList(Role.SalesRep);
        Assert.DoesNotContain(salesRep.Tables, t => t.StartsWith("HumanResources.", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Person.Password", salesRep.Tables);
        Assert.Contains("SalesLastYear", salesRep.DeniedColumns["Sales.SalesTerritory"]);
        Assert.Contains("SalesQuota", salesRep.DeniedColumns["Sales.SalesPerson"]);
        Assert.Equal(5, catalog.GetExamples(Role.SalesRep).Count);
        Assert.Equal(8, catalog.GetExamples(Role.Admin).Count);

        // Global denies reach the roles that read those tables.
        Assert.Contains("CardNumber", catalog.GetAllowList(Role.Admin).DeniedColumns["Sales.CreditCard"]);
        Assert.Contains("NationalIDNumber", catalog.GetAllowList(Role.Admin).DeniedColumns["HumanResources.Employee"]);

        // Column detail comes only from DescribeTables, with denied columns removed.
        var territory = catalog.DescribeTables(Role.SalesRep, ["Sales.SalesTerritory"]).Tables.Single();
        Assert.DoesNotContain(territory.Columns, c => c.Name is "SalesYTD" or "SalesLastYear" or "CostYTD" or "CostLastYear");
        Assert.Contains(territory.Columns, c => c.Name == "Name");
    }
}
