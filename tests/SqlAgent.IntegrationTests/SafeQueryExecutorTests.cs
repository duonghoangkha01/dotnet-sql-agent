using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SqlAgent.Core;
using SqlAgent.Core.Execution;
using SqlAgent.Core.Guardrails;
using SqlAgent.IntegrationTests.Fixtures;

namespace SqlAgent.IntegrationTests;

/// <summary>
/// The executor and the two database layers behind the guardrail (per-role users, row-level security), against
/// the real AdventureWorks. Queries go through the guardrail first, as the agent's tool will do; tests about the
/// database itself send SQL straight to the executor, which is exactly what a guardrail bug would allow.
/// </summary>
[Collection(AdventureWorksFixture.CollectionName)]
public class SafeQueryExecutorTests(AdventureWorksFixture db)
{
    private const string GenericFailure = "The query could not be run. Try a simpler query.";

    private static readonly SqlGuardrail Guardrail = new();

    private SafeQueryExecutor Executor => new(db.Connections);

    private async Task<QueryResult> RunAsync(Role role, string sql, int? territory = null)
    {
        var verdict = Guardrail.Validate(sql, db.AllowListFor(role));
        Assert.True(verdict.Allowed, string.Join(" | ", verdict.Violations.Select(v => v.Message)));
        return await Executor.ExecuteAsync(verdict.Sql!, new UserContext(role, territory));
    }

    [Fact]
    public async Task A_query_is_one_span_with_counts_timing_and_role_and_no_sql_text_or_rows()
    {
        var spans = new System.Collections.Concurrent.ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is SqlAgent.Core.Agent.AgentTelemetry.SourceName or "sql-span-test",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);
        using var testSource = new ActivitySource("sql-span-test");
        using var root = testSource.StartActivity("root")!;

        var result = await RunAsync(Role.Finance, "SELECT FirstName, LastName FROM Person.Person");

        var span = Assert.Single(spans, s => s.OperationName == "sql.execute" && s.TraceId == root.TraceId);
        Assert.Equal("finance", span.GetTagItem("role"));
        Assert.Equal(result.RowCount, span.GetTagItem("rowCount"));
        Assert.Equal(true, span.GetTagItem("truncated"));
        Assert.Equal(result.ElapsedMs, span.GetTagItem("elapsedMs"));
        Assert.Equal("ok", span.GetTagItem("outcome"));
        Assert.DoesNotContain(span.TagObjects, t => t.Value?.ToString()?.Contains("Person") == true || t.Value?.ToString()?.Contains("SELECT") == true);
    }

    private static int Count(QueryResult result) => Assert.IsType<int>(Assert.Single(result.Rows)[0]);

    private static int InnerSqlNumber(QueryExecutionException ex) => Assert.IsType<SqlException>(ex.InnerException).Number;

    // ---- row-level security: every territory-bearing table in docs/rls-coverage.md ----

    [Theory]
    [InlineData("Sales.SalesOrderHeader", "TerritoryID = 1")]
    [InlineData("Sales.Customer", "TerritoryID = 1")]
    [InlineData("Sales.SalesOrderDetail", "SalesOrderID IN (SELECT SalesOrderID FROM Sales.SalesOrderHeader WHERE TerritoryID = 1)")]
    [InlineData("Sales.SalesOrderHeaderSalesReason", "SalesOrderID IN (SELECT SalesOrderID FROM Sales.SalesOrderHeader WHERE TerritoryID = 1)")]
    [InlineData("Sales.Store", "SalesPersonID IN (SELECT BusinessEntityID FROM Sales.SalesPerson WHERE TerritoryID = 1)")]
    public async Task Row_level_security_isolates_territory_1_and_leaves_finance_and_admin_unrestricted(string table, string territoryOneRows)
    {
        // Expected counts come from sa (exempt from the policy), and must be a non-empty strict subset.
        var total = await db.ScalarAsSaAsync<int>($"SELECT COUNT(*) FROM {table}");
        var territoryOne = await db.ScalarAsSaAsync<int>($"SELECT COUNT(*) FROM {table} WHERE {territoryOneRows}");
        Assert.InRange(territoryOne, 1, total - 1);

        var query = $"SELECT COUNT(*) AS n FROM {table}";
        Assert.Equal(territoryOne, Count(await RunAsync(Role.SalesRep, query, territory: 1)));
        Assert.Equal(total, Count(await RunAsync(Role.Finance, query)));
        Assert.Equal(total, Count(await RunAsync(Role.Admin, query)));
    }

    [Fact]
    public async Task A_sales_rep_without_a_territory_sees_no_rows()
    {
        await using var connection = await db.Connections.OpenAsync(Role.SalesRep, default);
        await using var command = new SqlCommand("SELECT COUNT(*) FROM Sales.SalesOrderHeader", connection);

        Assert.Equal(0, (int)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public void A_sales_rep_context_requires_a_territory()
    {
        Assert.Throws<ArgumentException>(() => new UserContext(Role.SalesRep));
    }

    // ---- the database denies independently of the guardrail ----

    [Theory]
    [InlineData("sales_rep", "SELECT SalesYTD FROM Sales.SalesTerritory")]
    [InlineData("sales_rep", "SELECT SalesQuota FROM Sales.SalesPerson")]
    [InlineData("sales_rep", "SELECT Demographics FROM Person.Person")]
    [InlineData("sales_rep", "SELECT CardType FROM Sales.CreditCard")]
    [InlineData("sales_rep", "SELECT JobTitle FROM HumanResources.Employee")]
    [InlineData("finance", "SELECT JobTitle FROM HumanResources.Employee")]
    [InlineData("finance", "SELECT PasswordHash FROM Person.Password")]
    [InlineData("admin", "SELECT CardNumber FROM Sales.CreditCard")]
    [InlineData("admin", "SELECT NationalIDNumber FROM HumanResources.Employee")]
    public async Task Database_denies_what_the_role_may_not_read_even_without_the_guardrail(string roleKey, string sql)
    {
        Assert.True(RoleExtensions.TryParse(roleKey, out var role));
        var user = new UserContext(role, role == Role.SalesRep ? 1 : null);

        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => Executor.ExecuteAsync(sql, user));

        Assert.Equal(QueryFailure.Failed, ex.Failure);
        Assert.Equal(GenericFailure, ex.Message);
        Assert.Contains(InnerSqlNumber(ex), new[] { 229, 230 }); // SELECT permission denied on object / column
    }

    [Fact]
    public async Task The_territory_context_is_read_only()
    {
        await using var connection = await db.Connections.OpenAsync(Role.SalesRep, default);
        await Set(connection, 5, readOnly: true);

        var ex = await Assert.ThrowsAsync<SqlException>(() => Set(connection, 6, readOnly: true));
        Assert.Equal(15664, ex.Number); // key set as read_only for this session

        static async Task Set(SqlConnection c, int territory, bool readOnly)
        {
            await using var command = new SqlCommand(
                "EXEC sys.sp_set_session_context @key = N'territory_id', @value = @v, @read_only = @ro", c);
            command.Parameters.AddWithValue("@v", territory);
            command.Parameters.AddWithValue("@ro", readOnly);
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task User_sql_cannot_change_the_territory_the_executor_set()
    {
        var user = new UserContext(Role.SalesRep, 1);

        // Sent without the guardrail, which would refuse EXEC. The context was set read-only before this ran.
        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => Executor.ExecuteAsync(
            "EXEC sys.sp_set_session_context @key = N'territory_id', @value = 4", user));
        Assert.Equal(15664, InnerSqlNumber(ex));

        var expected = await db.ScalarAsSaAsync<int>("SELECT COUNT(*) FROM Sales.SalesOrderHeader WHERE TerritoryID = 1");
        Assert.Equal(expected, Count(await RunAsync(Role.SalesRep, "SELECT COUNT(*) AS n FROM Sales.SalesOrderHeader", 1)));
    }

    // ---- the guardrail and the server must read names the same way ----

    /// <summary>
    /// SQL Server ignores trailing spaces in identifiers, so a padded name reaches the denied column. This checks
    /// both halves: the server really resolves the padded name (else the guardrail rule would be dead weight), and
    /// the guardrail still refuses it for the role that may not read the column.
    /// </summary>
    [Theory]
    [InlineData("SELECT TOP 1 [SalesYTD ] FROM Sales.SalesTerritory")]
    [InlineData("SELECT TOP 1 t.[SalesYTD ] FROM Sales.SalesTerritory AS [t ]")]
    [InlineData("SELECT TOP 1 [SalesYTD] FROM [Sales ].[SalesTerritory ]")]
    public async Task A_padded_identifier_resolves_on_the_server_and_is_still_refused_by_the_guardrail(string sql)
    {
        await db.ScalarAsSaAsync<decimal>(sql); // throws if the server does not accept the name

        var verdict = Guardrail.Validate(sql, db.AllowListFor(Role.SalesRep));
        Assert.False(verdict.Allowed);
        Assert.Contains(verdict.Violations, v => v.Code == ViolationCode.DeniedColumn);
    }

    // ---- pooled connections ----

    [Fact]
    public async Task Pooled_connections_never_carry_one_territory_into_another_users_query()
    {
        int[] territories = [1, 4];
        var expected = new Dictionary<int, int>();
        foreach (var t in territories)
            expected[t] = await db.ScalarAsSaAsync<int>($"SELECT COUNT(*) FROM Sales.SalesOrderHeader WHERE TerritoryID = {t}");
        Assert.NotEqual(expected[1], expected[4]);

        const string query = "SELECT COUNT(*) AS n, MIN(TerritoryID) AS lo, MAX(TerritoryID) AS hi FROM Sales.SalesOrderHeader";

        // 20 at a time, several rounds: from the second round on, every connection comes back out of the pool.
        for (var round = 0; round < 4; round++)
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
            {
                var territory = territories[i % 2];
                var row = Assert.Single((await RunAsync(Role.SalesRep, query, territory)).Rows);
                return (territory, n: (int)row[0]!, lo: (int)row[1]!, hi: (int)row[2]!);
            }));

            Assert.All(results, r =>
            {
                Assert.Equal(expected[r.territory], r.n);
                Assert.Equal(r.territory, r.lo);
                Assert.Equal(r.territory, r.hi);
            });
        }
    }

    // ---- row cap, TOP injection, timeout ----

    [Fact]
    public async Task A_large_query_returns_500_rows_flagged_truncated_without_an_error()
    {
        var verdict = Guardrail.Validate("SELECT SalesOrderID, ProductID FROM Sales.SalesOrderDetail", db.AllowListFor(Role.Admin));
        Assert.Contains("TOP (501)", verdict.Sql); // injected by the guardrail, not by the executor

        var result = await Executor.ExecuteAsync(verdict.Sql!, new UserContext(Role.Admin));

        Assert.Equal(500, result.RowCount);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task A_union_has_no_top_and_is_capped_by_the_executor_and_the_connection_stays_usable()
    {
        const string union = "SELECT SalesOrderID FROM Sales.SalesOrderDetail UNION ALL SELECT SalesOrderID FROM Sales.SalesOrderDetail";
        Assert.DoesNotContain("TOP", Guardrail.Validate(union, db.AllowListFor(Role.Admin)).Sql);

        // Repeated, so a cancelled read-out is followed by reuse of the same pooled connection.
        for (var i = 0; i < 3; i++)
        {
            var result = await RunAsync(Role.Admin, union);
            Assert.Equal(500, result.RowCount);
            Assert.True(result.Truncated);
        }

        Assert.Equal(1, Count(await RunAsync(Role.Admin, "SELECT 1 AS one")));
    }

    [Theory]
    [InlineData(499, false)]
    [InlineData(500, false)]
    [InlineData(501, true)]
    public async Task Truncated_means_more_rows_exist_than_were_returned(int top, bool truncated)
    {
        var result = await RunAsync(Role.Admin, $"SELECT TOP {top} SalesOrderID FROM Sales.SalesOrderDetail ORDER BY SalesOrderID");

        Assert.Equal(Math.Min(top, 500), result.RowCount);
        Assert.Equal(truncated, result.Truncated);
    }

    /// <summary>
    /// The row cap alone does not bound a result: an aggregate with no GROUP BY returns the whole allowed table in
    /// one row. STRING_AGG over every line total is on the allowlist and passes the guardrail (it names no denied
    /// table or column); without a byte cap it would hand back megabytes in a single "row", defeating the cap.
    /// Cast to varchar(max): a non-LOB STRING_AGG result is separately capped by the server itself at 8,000 bytes,
    /// which would mask the application-level cap this test is for.
    /// </summary>
    [Fact]
    public async Task An_aggregate_with_no_group_by_is_capped_by_result_size_not_row_count()
    {
        var result = await RunAsync(Role.Admin, "SELECT STRING_AGG(CAST(LineTotal AS varchar(max)), ',') AS Blob FROM Sales.SalesOrderDetail");

        Assert.True(result.Truncated);
        Assert.Empty(result.Rows); // cut before the one huge row was kept
    }

    /// <summary>A single REPLICATE'd cell is the same shape of risk as STRING_AGG: one row, unbounded size.</summary>
    [Fact]
    public async Task A_single_huge_cell_from_replicate_is_also_capped_by_result_size()
    {
        var result = await RunAsync(Role.Admin, "SELECT REPLICATE(CAST('x' AS varchar(max)), 50000000) AS Huge");

        Assert.True(result.Truncated);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task A_query_over_the_time_limit_is_cancelled_with_a_timeout_error()
    {
        var stopwatch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() =>
            Executor.ExecuteAsync("WAITFOR DELAY '00:00:40'", new UserContext(Role.Admin)));

        Assert.Equal(QueryFailure.Timeout, ex.Failure);
        Assert.Contains("15 seconds", ex.Message);
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 14, 25);
    }

    [Fact]
    public async Task Cancelling_the_token_cancels_the_query()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Executor.ExecuteAsync("WAITFOR DELAY '00:00:20'", new UserContext(Role.Admin), cts.Token));
    }

    // ---- results and error mapping ----

    [Fact]
    public async Task Columns_and_nulls_come_back_typed()
    {
        var result = await RunAsync(Role.Admin, "SELECT ProductID, ListPrice, Color FROM Production.Product WHERE Color IS NULL");

        Assert.Equal(["ProductID", "ListPrice", "Color"], result.Columns.Select(c => c.Name));
        Assert.Equal(["int", "money"], result.Columns.Take(2).Select(c => c.Type));
        Assert.All(result.Rows, row => Assert.Null(row[2]));
        Assert.False(result.Truncated);
        Assert.True(result.ElapsedMs >= 0);
    }

    [Theory]
    [InlineData("SELECT TOP 1 SpatialLocation FROM Person.Address", "geography")]
    [InlineData("SELECT TOP 1 OrganizationNode FROM HumanResources.Employee", "hierarchyid")]
    public async Task A_column_of_a_clr_type_gets_a_clear_error_and_the_connection_stays_usable(string sql, string type)
    {
        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => RunAsync(Role.Admin, sql));

        Assert.Equal(QueryFailure.InvalidQuery, ex.Failure);
        Assert.Contains(type, ex.Message);
        Assert.Equal(1, Count(await RunAsync(Role.Admin, "SELECT 1 AS one")));
    }

    [Fact]
    public async Task A_hierarchyid_converted_to_text_can_be_returned()
    {
        var result = await RunAsync(Role.Admin,
            "SELECT TOP 1 CAST(OrganizationNode AS nvarchar(100)) AS Node FROM HumanResources.Employee WHERE OrganizationNode IS NOT NULL");

        Assert.IsType<string>(Assert.Single(result.Rows)[0]);
    }

    [Theory]
    [InlineData("SELECT FROM Sales.Customer", "Incorrect syntax")]
    [InlineData("SELECT nope FROM Sales.Customer", "Invalid column name 'nope'")]
    public async Task Errors_about_the_query_itself_are_passed_on_for_the_model_to_fix(string sql, string expected)
    {
        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => Executor.ExecuteAsync(sql, new UserContext(Role.Admin)));

        Assert.Equal(QueryFailure.InvalidQuery, ex.Failure);
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Other_errors_become_a_generic_message_without_server_text()
    {
        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => Executor.ExecuteAsync("SELECT 1 / 0 AS x", new UserContext(Role.Admin)));

        Assert.Equal(QueryFailure.Failed, ex.Failure);
        Assert.Equal(GenericFailure, ex.Message);
    }
}
