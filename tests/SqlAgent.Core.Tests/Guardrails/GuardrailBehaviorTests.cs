using System.Diagnostics;
using SqlAgent.Core.Guardrails;
using SqlAgent.Core.Schema;
using Xunit.Abstractions;

namespace SqlAgent.Core.Tests.Guardrails;

/// <summary>What the guardrail does to a query it allows (row cap, regeneration) and its own limits.</summary>
public class GuardrailBehaviorTests(ITestOutputHelper output)
{
    private static readonly SqlGuardrail Guardrail = new();
    private static readonly AllowList SalesRep = GuardrailTestData.AllowList(Role.SalesRep);

    private static GuardrailResult Validate(string sql) => Guardrail.Validate(sql, SalesRep);

    [Fact]
    public void Plain_select_gets_top_max_rows_plus_one()
    {
        var result = Validate("SELECT CustomerID FROM Sales.Customer");

        Assert.True(result.Allowed);
        Assert.Contains("TOP (501)", result.Sql);
    }

    [Fact]
    public void Row_cap_follows_the_configured_limit()
    {
        var result = new SqlGuardrail(new QueryLimits(MaxRows: 20)).Validate("SELECT CustomerID FROM Sales.Customer", SalesRep);

        Assert.Contains("TOP (21)", result.Sql);
    }

    [Fact]
    public void An_existing_top_is_kept_as_it_is()
    {
        var result = Validate("SELECT TOP 5 CustomerID FROM Sales.Customer ORDER BY CustomerID");

        Assert.Contains("TOP 5", result.Sql);
        Assert.DoesNotContain("501", result.Sql);
    }

    [Theory]
    [InlineData("SELECT CustomerID FROM Sales.Customer UNION ALL SELECT CustomerID FROM Sales.SalesOrderHeader")]
    [InlineData("SELECT CustomerID FROM Sales.Customer ORDER BY CustomerID OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY")]
    public void Set_operations_and_offset_are_left_to_the_executor_cap(string sql)
    {
        var result = Validate(sql);

        Assert.True(result.Allowed);
        Assert.DoesNotContain("TOP (501)", result.Sql);
    }

    [Fact]
    public void Top_goes_on_the_outer_query_only()
    {
        var result = Validate("WITH c AS (SELECT CustomerID FROM Sales.Customer) SELECT CustomerID FROM c");

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result.Sql!, "TOP"));
        Assert.Matches(@"SELECT\s+TOP \(501\)\s+CustomerID\s+FROM\s+c", result.Sql);
    }

    [Fact]
    public void The_sql_that_runs_is_the_regenerated_tree_without_comments()
    {
        var result = Validate("SELECT CustomerID /* hidden */ FROM Sales.Customer -- trailing");

        Assert.DoesNotContain("hidden", result.Sql);
        Assert.DoesNotContain("trailing", result.Sql);
    }

    [Fact]
    public void Output_is_a_fixed_point_and_deterministic()
    {
        var once = Validate("select customerid from sales.customer where customerid > 5").Sql!;
        var again = Validate(once);

        Assert.Equal(once, Validate("select customerid from sales.customer where customerid > 5").Sql);
        Assert.True(again.Allowed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    [InlineData("-- only a comment")]
    public void Empty_input_is_refused(string? sql)
    {
        var result = Guardrail.Validate(sql, SalesRep);

        Assert.False(result.Allowed);
        Assert.Contains(result.Violations, v => v.Code == ViolationCode.NotASelect);
    }

    [Fact]
    public void Oversized_query_is_refused_before_parsing()
    {
        var sql = "SELECT " + string.Join("+", Enumerable.Repeat("1", SqlGuardrail.MaxSqlLength)) + " AS n";

        Assert.Contains(Validate(sql).Violations, v => v.Code == ViolationCode.TooComplex);
    }

    [Fact]
    public void Deeply_nested_query_is_refused_instead_of_overflowing_the_stack()
    {
        var sql = "SELECT " + new string('(', 1500) + "1" + new string(')', 1500) + " AS n";

        Assert.Contains(Validate(sql).Violations, v => v.Code == ViolationCode.TooComplex);
    }

    [Fact]
    public void A_query_at_the_nesting_limit_still_passes()
    {
        var depth = SqlGuardrail.MaxNestingDepth;
        var sql = "SELECT " + new string('(', depth) + "1" + new string(')', depth) + " AS n";

        Assert.True(Validate(sql).Allowed);
    }

    [Fact]
    public void Many_violations_are_all_reported_once_each()
    {
        var result = Validate("SELECT SalesYTD, SalesYTD FROM Sales.SalesTerritory, HumanResources.Employee");

        Assert.Contains(result.Violations, v => v.Code == ViolationCode.DeniedColumn);
        Assert.Contains(result.Violations, v => v.Code == ViolationCode.TableNotAllowed);
        Assert.Equal(result.Violations.Count, result.Violations.Distinct().Count());
    }

    [Fact]
    public void Concurrent_validation_gives_the_same_answers()
    {
        var expected = Validate("SELECT CustomerID FROM Sales.Customer").Sql;

        var results = Enumerable.Range(0, 200).AsParallel()
            .Select(i => i % 2 == 0
                ? Validate("SELECT CustomerID FROM Sales.Customer").Sql
                : Validate("SELECT SalesYTD FROM Sales.SalesTerritory").Sql)
            .ToList();

        Assert.Equal(100, results.Count(r => r == expected));
        Assert.Equal(100, results.Count(r => r is null));
    }

    /// <summary>Records the p50 for the README. A loose bound catches a regression; it is not the p50 target itself.</summary>
    [Fact]
    public void Validation_latency_p50_is_recorded()
    {
        var queries = new[]
        {
            "SELECT TOP 5 p.Name, SUM(d.LineTotal) AS Revenue FROM Sales.SalesOrderDetail AS d JOIN Production.Product AS p ON p.ProductID = d.ProductID GROUP BY p.Name ORDER BY Revenue DESC",
            "SELECT c.Name AS Category, SUM(d.LineTotal) AS Revenue FROM Sales.SalesOrderDetail AS d JOIN Production.Product AS p ON p.ProductID = d.ProductID JOIN Production.ProductSubcategory AS s ON s.ProductSubcategoryID = p.ProductSubcategoryID JOIN Production.ProductCategory AS c ON c.ProductCategoryID = s.ProductCategoryID GROUP BY c.Name ORDER BY Revenue DESC",
            "WITH yearly AS (SELECT YEAR(OrderDate) AS Y, SUM(TotalDue) AS Total FROM Sales.SalesOrderHeader GROUP BY YEAR(OrderDate)) SELECT Y, Total, LAG(Total) OVER (ORDER BY Y) AS Prev FROM yearly ORDER BY Y",
        };

        for (var i = 0; i < 200; i++) Validate(queries[i % queries.Length]); // warm-up: JIT and parser tables

        var timings = new List<double>();
        for (var i = 0; i < 1000; i++)
        {
            var start = Stopwatch.GetTimestamp();
            Validate(queries[i % queries.Length]);
            timings.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        timings.Sort();
        output.WriteLine($"guardrail p50 = {timings[timings.Count / 2]:F2} ms, p95 = {timings[(int)(timings.Count * 0.95)]:F2} ms over {timings.Count} validations");
        Assert.True(timings[timings.Count / 2] < 25, $"p50 {timings[timings.Count / 2]:F2} ms");
    }
}
