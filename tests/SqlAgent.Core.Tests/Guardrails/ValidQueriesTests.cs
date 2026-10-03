using SqlAgent.Core.Guardrails;

namespace SqlAgent.Core.Tests.Guardrails;

/// <summary>Legitimate analytical queries that must pass. An over-blocked query is added here, never "fixed" in the corpus.</summary>
public class ValidQueriesTests
{
    private static readonly SqlGuardrail Guardrail = new();

    public static TheoryData<string, string> Queries => new()
    {
        // Joins and aggregates
        { "sales_rep", "SELECT YEAR(OrderDate) AS OrderYear, COUNT(*) AS OrderCount FROM Sales.SalesOrderHeader GROUP BY YEAR(OrderDate) ORDER BY OrderYear" },
        { "sales_rep", "SELECT TOP 5 p.Name, SUM(d.LineTotal) AS Revenue FROM Sales.SalesOrderDetail AS d JOIN Production.Product AS p ON p.ProductID = d.ProductID GROUP BY p.Name ORDER BY Revenue DESC" },
        { "sales_rep", "SELECT c.Name AS Category, SUM(d.LineTotal) AS Revenue FROM Sales.SalesOrderDetail AS d JOIN Production.Product AS p ON p.ProductID = d.ProductID JOIN Production.ProductSubcategory AS s ON s.ProductSubcategoryID = p.ProductSubcategoryID JOIN Production.ProductCategory AS c ON c.ProductCategoryID = s.ProductCategoryID GROUP BY c.Name HAVING SUM(d.LineTotal) > 1000 ORDER BY Revenue DESC" },
        { "sales_rep", "SELECT t.Name AS Territory, COUNT(*) AS OrderCount FROM Sales.SalesOrderHeader AS h JOIN Sales.SalesTerritory AS t ON t.TerritoryID = h.TerritoryID GROUP BY t.Name" },
        { "sales_rep", "SELECT h.SalesOrderID, h.TotalDue FROM Sales.SalesOrderHeader AS h LEFT JOIN Sales.SalesPerson AS sp ON sp.BusinessEntityID = h.SalesPersonID WHERE sp.BusinessEntityID IS NULL" },
        { "sales_rep", "SELECT COUNT(DISTINCT CustomerID) AS Customers, MIN(OrderDate) AS FirstOrder, MAX(OrderDate) AS LastOrder FROM Sales.SalesOrderHeader" },
        { "sales_rep", "SELECT Sales.SalesOrderHeader.SalesOrderID FROM Sales.SalesOrderHeader" },
        { "sales_rep", "SELECT SalesOrderHeader.SalesOrderID FROM Sales.SalesOrderHeader" },
        { "sales_rep", "SELECT \"SalesOrderID\" FROM \"Sales\".\"SalesOrderHeader\"" },
        { "sales_rep", "SELECT [SalesOrderID] FROM [Sales].[SalesOrderHeader];" },

        // CTEs (including recursive-style chaining and a CTE named like a table)
        { "sales_rep", "WITH yearly AS (SELECT YEAR(OrderDate) AS Y, SUM(TotalDue) AS Total FROM Sales.SalesOrderHeader GROUP BY YEAR(OrderDate)) SELECT Y, Total FROM yearly ORDER BY Y" },
        { "sales_rep", "WITH a AS (SELECT CustomerID FROM Sales.Customer), b AS (SELECT CustomerID FROM a) SELECT COUNT(*) AS n FROM b" },
        { "sales_rep", "WITH SalesOrderHeader AS (SELECT SalesOrderID FROM Sales.SalesOrderHeader) SELECT COUNT(*) AS n FROM SalesOrderHeader" },
        { "sales_rep", "WITH nums AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM nums WHERE n < 10) SELECT n FROM nums" },

        // Window functions
        { "sales_rep", "SELECT SalesOrderID, TotalDue, ROW_NUMBER() OVER (PARTITION BY CustomerID ORDER BY OrderDate DESC) AS rn FROM Sales.SalesOrderHeader" },
        { "sales_rep", "SELECT OrderDate, SUM(TotalDue) OVER (ORDER BY OrderDate ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS Running FROM Sales.SalesOrderHeader" },
        { "sales_rep", "SELECT SalesOrderID, LAG(TotalDue) OVER (ORDER BY OrderDate) AS Prev, RANK() OVER (ORDER BY TotalDue DESC) AS rk FROM Sales.SalesOrderHeader" },

        // Subqueries
        { "sales_rep", "SELECT c.CustomerID FROM Sales.Customer AS c WHERE EXISTS (SELECT 1 FROM Sales.SalesOrderHeader AS h WHERE h.CustomerID = c.CustomerID AND h.TotalDue > 1000)" },
        { "sales_rep", "SELECT c.CustomerID FROM Sales.Customer AS c WHERE c.CustomerID IN (SELECT h.CustomerID FROM Sales.SalesOrderHeader AS h)" },
        { "sales_rep", "SELECT h.SalesOrderID, (SELECT COUNT(*) FROM Sales.SalesOrderDetail AS d WHERE d.SalesOrderID = h.SalesOrderID) AS Lines FROM Sales.SalesOrderHeader AS h" },
        { "sales_rep", "SELECT x.CustomerID, x.Total FROM (SELECT CustomerID, SUM(TotalDue) AS Total FROM Sales.SalesOrderHeader GROUP BY CustomerID) AS x WHERE x.Total > 5000" },
        { "sales_rep", "SELECT h.SalesOrderID FROM Sales.SalesOrderHeader AS h WHERE h.TotalDue > ALL (SELECT TotalDue FROM Sales.SalesOrderHeader WHERE CustomerID = 1)" },

        // Set operations, expressions and functions
        { "sales_rep", "SELECT CustomerID FROM Sales.Customer UNION ALL SELECT CustomerID FROM Sales.SalesOrderHeader" },
        { "sales_rep", "SELECT CustomerID FROM Sales.Customer EXCEPT SELECT CustomerID FROM Sales.SalesOrderHeader" },
        { "sales_rep", "SELECT CASE WHEN TotalDue > 1000 THEN 'big' WHEN TotalDue > 100 THEN 'medium' ELSE 'small' END AS Size, COUNT(*) AS n FROM Sales.SalesOrderHeader GROUP BY CASE WHEN TotalDue > 1000 THEN 'big' WHEN TotalDue > 100 THEN 'medium' ELSE 'small' END" },
        { "sales_rep", "SELECT CASE Status WHEN 5 THEN 'shipped' ELSE 'open' END AS S FROM Sales.SalesOrderHeader" },
        { "sales_rep", "SELECT CAST(TotalDue AS decimal(10, 2)) AS a, CONVERT(varchar(10), OrderDate, 120) AS d, TRY_CAST(Freight AS int) AS f, TRY_CONVERT(nvarchar(max), TaxAmt) AS t FROM Sales.SalesOrderHeader" },
        { "sales_rep", "SELECT COALESCE(Color, 'n/a') AS c, NULLIF(Size, '') AS s, IIF(ListPrice > 100, 'hi', 'lo') AS p, ISNULL(Weight, 0) AS w FROM Production.Product" },
        { "sales_rep", "SELECT DATEADD(month, -1, GETDATE()) AS m, DATEDIFF(day, OrderDate, ShipDate) AS d, DATEPART(year, OrderDate) AS y, EOMONTH(OrderDate) AS e, DATENAME(month, OrderDate) AS n FROM Sales.SalesOrderHeader" },
        { "sales_rep", "SELECT LEFT(Name, 3) AS l, RIGHT(Name, 2) AS r, UPPER(Name) AS u, LEN(Name) AS n, SUBSTRING(Name, 1, 2) AS s, REPLACE(Name, 'a', 'b') AS x, CONCAT(Name, '-', ProductNumber) AS c, CURRENT_TIMESTAMP AS now FROM Production.Product" },
        { "sales_rep", "SELECT ROUND(ListPrice * 1.1, 2) AS a, ABS(ListPrice - StandardCost) AS b, CEILING(ListPrice) AS c, POWER(ListPrice, 2) AS d FROM Production.Product" },
        { "sales_rep", "SELECT ProductID FROM Production.Product WHERE Name LIKE 'Mountain%' AND ListPrice BETWEEN 100 AND 500 AND Color IS NOT NULL AND ProductSubcategoryID IN (1, 2, 3) AND NOT (Size = 'M')" },
        { "sales_rep", "SELECT Color, STRING_AGG(Name, ', ') WITHIN GROUP (ORDER BY Name) AS Names FROM Production.Product GROUP BY Color" },
        { "sales_rep", "SELECT Color, Size, COUNT(*) AS n FROM Production.Product GROUP BY ROLLUP (Color, Size)" },
        { "sales_rep", "SELECT SalesOrderID FROM Sales.SalesOrderHeader ORDER BY OrderDate DESC OFFSET 10 ROWS FETCH NEXT 20 ROWS ONLY" },
        { "sales_rep", "SELECT DISTINCT TOP 10 Color FROM Production.Product ORDER BY Color" },
        { "sales_rep", "SELECT TOP (10) PERCENT Name FROM Production.Product ORDER BY Name" },
        { "sales_rep", "SELECT a.Name, b.Name AS Other FROM Production.Product AS a CROSS JOIN Production.ProductCategory AS b" },
        { "sales_rep", "SELECT c.CustomerID, x.OrderId FROM Sales.Customer AS c OUTER APPLY (SELECT TOP 1 h.SalesOrderID AS OrderId FROM Sales.SalesOrderHeader AS h WHERE h.CustomerID = c.CustomerID ORDER BY h.OrderDate DESC) AS x" },
        { "sales_rep", "SELECT 1 AS one" },

        // Allowed columns of tables that have denied ones, and a select-list alias that reuses a denied name
        { "sales_rep", "SELECT t.Name, t.CountryRegionCode, t.[Group] FROM Sales.SalesTerritory AS t" },
        { "sales_rep", "SELECT sp.BusinessEntityID, sp.TerritoryID FROM Sales.SalesPerson AS sp" },
        { "sales_rep", "SELECT p.FirstName, p.LastName FROM Person.Person AS p" },
        { "sales_rep", "SELECT SUM(TotalDue) AS SalesYTD FROM Sales.SalesOrderHeader" },

        // Roles that see more
        { "finance", "SELECT Name, SalesYTD, SalesLastYear FROM Sales.SalesTerritory ORDER BY SalesYTD DESC" },
        { "finance", "SELECT TOP 10 v.Name, SUM(h.TotalDue) AS TotalSpent FROM Purchasing.PurchaseOrderHeader AS h JOIN Purchasing.Vendor AS v ON v.BusinessEntityID = h.VendorID GROUP BY v.Name ORDER BY TotalSpent DESC" },
        { "admin", "SELECT JobTitle, COUNT(*) AS EmployeeCount FROM HumanResources.Employee GROUP BY JobTitle ORDER BY EmployeeCount DESC" },
        { "admin", "SELECT CardType, ExpYear FROM Sales.CreditCard" },
    };

    private static Role RoleOf(string key) => RoleExtensions.TryParse(key, out var role) ? role : throw new ArgumentException(key);

    [Theory]
    [MemberData(nameof(Queries))]
    public void Legitimate_query_is_allowed(string role, string sql)
    {
        var result = Guardrail.Validate(sql, GuardrailTestData.AllowList(RoleOf(role)));

        Assert.True(result.Allowed, string.Join(" | ", result.Violations.Select(v => $"{v.Code}: {v.Message}")));
        Assert.False(string.IsNullOrWhiteSpace(result.Sql));
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Query_set_has_at_least_20_cases()
    {
        Assert.True(Queries.Count >= 20, $"only {Queries.Count} cases");
    }

    [Theory]
    [InlineData("sales_rep")]
    [InlineData("finance")]
    [InlineData("admin")]
    public void Every_shipped_example_passes_for_each_role_it_is_tagged_with(string roleKey)
    {
        var role = RoleOf(roleKey);
        var examples = GuardrailTestData.Catalog.GetExamples(role);
        Assert.NotEmpty(examples);

        foreach (var example in examples)
        {
            var result = Guardrail.Validate(example.Sql, GuardrailTestData.AllowList(role));
            Assert.True(result.Allowed, $"{example.Question}: " + string.Join(" | ", result.Violations.Select(v => v.Message)));
        }
    }
}
