using SqlAgent.Core.Schema;

namespace SqlAgent.Core.Tests.Schema;

/// <summary>A small fake database and semantic.yaml, so the catalog is tested without SQL Server.</summary>
internal static class SchemaTestData
{
    public const string SourcePath = "test/semantic.yaml";

    public static ColumnInfo Col(string name, string type = "int", string? description = null) =>
        new(name, type, IsNullable: false, description);

    public static TableInfo Table(string qualifiedName, string[] columns, params ForeignKeyInfo[] foreignKeys)
    {
        var parts = NameNormalizer.SplitParts(qualifiedName);
        return new TableInfo(parts[0], parts[1], null, columns.Select(c => Col(c)).ToList(), foreignKeys);
    }

    public static DatabaseSchema Schema() => new(
    [
        Table("Sales.SalesOrderHeader", ["SalesOrderID", "CustomerID", "TerritoryID", "TotalDue"],
            new ForeignKeyInfo("FK_Header_Territory", ["TerritoryID"], "Sales.SalesTerritory", ["TerritoryID"]),
            new ForeignKeyInfo("FK_Header_CreditCard", ["CustomerID"], "Sales.CreditCard", ["CreditCardID"])),
        Table("Sales.SalesTerritory", ["TerritoryID", "Name", "SalesYTD", "SalesLastYear"]),
        Table("Sales.CreditCard", ["CreditCardID", "CardType", "CardNumber"]),
        Table("Person.Person", ["BusinessEntityID", "FirstName", "Demographics"]),
        Table("Person.Password", ["BusinessEntityID", "PasswordHash", "PasswordSalt"]),
        Table("HumanResources.Employee", ["BusinessEntityID", "JobTitle", "NationalIDNumber"]),
    ]);

    /// <summary>Three roles: sales_rep (narrow), finance (adds credit cards) and admin (adds HR and passwords).</summary>
    public static readonly string Yaml = """
        roles:
          sales_rep:
            tables: [Sales.SalesOrderHeader, Sales.SalesTerritory, Person.Person]
            deny_columns:
              Sales.SalesTerritory: [SalesYTD, SalesLastYear]
              Person.Person: [Demographics]
          finance:
            tables: [Sales.SalesOrderHeader, Sales.SalesTerritory, Sales.CreditCard]
          admin:
            tables: [Sales.SalesOrderHeader, "[Sales].[SalesTerritory]", Sales.CreditCard, HumanResources.Employee, Person.Person, Person.Password]
        global_deny_columns:
          Sales.CreditCard: [CardNumber]
          HumanResources.Employee: [NationalIDNumber]
          Person.*: ["Password*"]
        tables:
          Sales.SalesOrderHeader:
            description: One row per order.
            synonyms: { revenue: TotalDue }
          Sales.SalesTerritory:
            description: Sales territories.
            synonyms: { "year to date": SalesYTD, region: Name }
        join_hints:
          - tables: [Sales.SalesOrderHeader, Sales.SalesTerritory]
            hint: Join on TerritoryID.
          - tables: [Sales.SalesOrderHeader, Sales.CreditCard]
            hint: Card details are finance-only.
        examples:
          - roles: [sales_rep, finance, admin]
            question: Orders per territory?
            sql: >-
              SELECT t.Name, COUNT(*) AS Orders
              FROM Sales.SalesOrderHeader AS h
              JOIN Sales.SalesTerritory AS t ON t.TerritoryID = h.TerritoryID
              GROUP BY t.Name
          - roles: [finance, admin]
            question: Territory revenue totals?
            sql: SELECT Name, SalesYTD FROM Sales.SalesTerritory
        """.ReplaceLineEndings("\n");

    public static SemanticConfig Config(string? yaml = null) => SemanticConfigParser.Parse(yaml ?? Yaml, SourcePath);

    public static SchemaCatalog Catalog(string? yaml = null, DatabaseSchema? schema = null) =>
        SemanticLayerLoader.Build(Config(yaml), schema ?? Schema());
}
