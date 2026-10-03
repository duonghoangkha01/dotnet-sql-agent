using System.Text.Json;
using Microsoft.Extensions.AI;
using SqlAgent.Core.Agent;
using SqlAgent.Core.Execution;
using SqlAgent.Core.Guardrails;
using SqlAgent.Core.Tests.Agent.Fakes;

namespace SqlAgent.Core.Tests.Agent;

public class SqlAgentToolsTests
{
    private const string ValidSql = "SELECT SalesOrderID FROM Sales.SalesOrderHeader";

    private static readonly UserContext Rep7 = new(Role.SalesRep, 7);

    private sealed record Setup(
        SqlAgentTools Tools, ListEventSink Events, RecordingAuditSink Audit, FakeQueryExecutor Executor)
    {
        public AIFunction Tool(string name) => Tools.Tools.OfType<AIFunction>().Single(t => t.Name == name);

        public async Task<string> RunSql(string sql, string purpose = "test") =>
            ((JsonElement)(await Tool("run_sql").InvokeAsync(new AIFunctionArguments { ["sql"] = sql, ["purpose"] = purpose }))!).GetString()!;
    }

    private static Setup Create(
        AgentOptions? options = null,
        Func<string, UserContext, CancellationToken, Task<QueryResult>>? run = null,
        UserContext? user = null)
    {
        var events = new ListEventSink();
        var audit = new RecordingAuditSink();
        var executor = new FakeQueryExecutor(run);
        var tools = new SqlAgentTools(
            new TurnContext(user ?? Rep7, "demo-sales-rep-nw", Guid.NewGuid(), "How many orders?", "trace1"),
            new StubSchemaCatalog(), new SqlGuardrail(), executor, audit, events, options ?? new AgentOptions());
        return new Setup(tools, events, audit, executor);
    }

    private static Task<QueryResult> Fail(string sql, UserContext user, CancellationToken ct) =>
        throw new QueryExecutionException(QueryFailure.InvalidQuery, "Invalid column name 'Nope'.");

    [Fact]
    public void The_model_can_only_supply_names_sql_and_purpose()
    {
        var setup = Create();

        var parameters = setup.Tools.Tools.OfType<AIFunction>()
            .SelectMany(t => t.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name))
            .Distinct().Order().ToList();

        Assert.Equal(["names", "purpose", "sql"], parameters);
        Assert.Equal(["describe_tables", "list_tables", "run_sql"], setup.Tools.Tools.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task Queries_run_for_the_server_side_user_not_one_the_model_names()
    {
        var setup = Create(user: new UserContext(Role.SalesRep, 7));

        await setup.RunSql(ValidSql);

        var call = Assert.Single(setup.Executor.Calls);
        Assert.Equal(Role.SalesRep, call.User.Role);
        Assert.Equal(7, call.User.TerritoryId);
    }

    [Fact]
    public async Task The_guardrail_sees_the_allow_list_of_the_callers_role()
    {
        var sales = Create(user: Rep7);
        var finance = Create(user: new UserContext(Role.Finance));
        const string territory = "SELECT TerritoryID FROM Sales.SalesTerritory";

        var blocked = await sales.RunSql(territory);
        await finance.RunSql(territory);

        Assert.Contains("blocked", blocked);
        Assert.Empty(sales.Executor.Calls);
        Assert.Single(finance.Executor.Calls);
    }

    [Fact]
    public async Task What_runs_is_the_regenerated_sql_with_the_row_cap()
    {
        var setup = Create();

        await setup.RunSql(ValidSql);

        Assert.Contains("TOP (501)", setup.Executor.Calls.Single().Sql);
    }

    [Fact]
    public async Task A_successful_query_emits_tool_call_then_query_result_and_one_audit_row_with_final_figures()
    {
        var setup = Create(run: (_, _, _) => Task.FromResult(FakeQueryExecutor.Rows(4, truncated: true)));

        await setup.RunSql(ValidSql, "count orders");

        var events = setup.Events.Events;
        var call = Assert.IsType<ToolCallEvent>(events[0]);
        Assert.Equal("run_sql", call.Tool);
        Assert.Equal("count orders", call.Purpose);
        var result = Assert.IsType<QueryResultEvent>(events[1]);
        Assert.Equal(call.CallId, result.CallId);
        Assert.Equal(4, result.RowCount);
        Assert.True(result.Truncated);
        Assert.Equal(4, result.Rows.Count);

        var audit = Assert.Single(setup.Audit.Entries);
        Assert.True(audit.Allowed);
        Assert.Equal(4, audit.ReturnedRows);
        Assert.Equal(3, audit.ElapsedMs);
        Assert.Equal("How many orders?", audit.Question);
        Assert.Equal("trace1", audit.TraceId);
        Assert.Equal("demo-sales-rep-nw", audit.Sub);
    }

    [Fact]
    public async Task A_refused_query_emits_guardrail_blocked_audits_it_and_never_reaches_the_database()
    {
        var setup = Create();

        var reply = await setup.RunSql("DELETE FROM Sales.SalesOrderHeader");

        Assert.Empty(setup.Executor.Calls);
        var blocked = Assert.Single(setup.Events.Of<GuardrailBlockedEvent>());
        Assert.Equal("NotASelect", Assert.Single(blocked.Violations).Code);
        var audit = Assert.Single(setup.Audit.Entries);
        Assert.False(audit.Allowed);
        Assert.Equal(ViolationCode.NotASelect, Assert.Single(audit.Violations).Code);
        Assert.Null(audit.ReturnedRows);
        Assert.Contains("\"blocked\":true", reply);
    }

    [Fact]
    public async Task A_database_error_emits_query_error_and_tells_the_model_the_message()
    {
        var setup = Create(run: Fail);

        var reply = await setup.RunSql(ValidSql);

        Assert.Contains("Invalid column name 'Nope'.", reply);
        Assert.Equal("Invalid column name 'Nope'.", Assert.Single(setup.Events.Of<QueryErrorEvent>()).Message);
        Assert.Null(Assert.Single(setup.Audit.Entries).ReturnedRows);
    }

    [Fact]
    public async Task An_unexpected_failure_is_generic_to_the_model_and_the_user()
    {
        var setup = Create(run: (_, _, _) => throw new InvalidOperationException("secret connection detail"));

        var reply = await setup.RunSql(ValidSql);

        Assert.DoesNotContain("secret", reply);
        Assert.DoesNotContain("secret", Assert.Single(setup.Events.Of<QueryErrorEvent>()).Message);
    }

    [Fact]
    public async Task A_cancelled_query_is_audited_and_the_cancellation_propagates()
    {
        var setup = Create(run: (_, _, _) => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.RunSql(ValidSql));

        Assert.Single(setup.Audit.Entries);
    }

    private static readonly QueryResult SixtyRows = FakeQueryExecutor.Rows(60);

    [Theory]
    [InlineData(ResultVisibility.None, 0)]
    [InlineData(ResultVisibility.Summary, 5)]
    [InlineData(ResultVisibility.Rows, 50)]
    public async Task The_model_sees_only_as_many_rows_as_the_visibility_allows(ResultVisibility visibility, int expectedRows)
    {
        var setup = Create(new AgentOptions { ResultVisibility = visibility }, (_, _, _) => Task.FromResult(SixtyRows));

        using var reply = JsonDocument.Parse(await setup.RunSql(ValidSql));

        var root = reply.RootElement;
        Assert.Equal(["Id", "Name"], root.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(60, root.GetProperty("rowCount").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(expectedRows,
            root.TryGetProperty("rows", out var rows) ? rows.GetArrayLength() : 0);
        // The UI still gets all of them.
        Assert.Equal(60, Assert.Single(setup.Events.Of<QueryResultEvent>()).Rows.Count);
    }

    [Fact]
    public async Task The_truncation_flag_reaches_the_model_in_every_mode()
    {
        foreach (var visibility in Enum.GetValues<ResultVisibility>())
        {
            var setup = Create(new AgentOptions { ResultVisibility = visibility },
                (_, _, _) => Task.FromResult(FakeQueryExecutor.Rows(2, truncated: true)));

            Assert.Contains("\"truncated\":true", await setup.RunSql(ValidSql));
        }
    }

    [Fact]
    public async Task Three_failures_in_a_row_end_with_a_stop_instruction_and_later_calls_do_not_run()
    {
        var setup = Create(run: Fail);

        var first = await setup.RunSql(ValidSql);
        var second = await setup.RunSql(ValidSql);
        var third = await setup.RunSql(ValidSql);
        var fourth = await setup.RunSql(ValidSql);

        Assert.DoesNotContain("STOP", first);
        Assert.DoesNotContain("STOP", second);
        Assert.Contains(SqlAgentTools.StopMessage, third);
        Assert.Equal(SqlAgentTools.StopMessage, fourth);
        // The call that was not run still gets an outcome, so a UI never shows a tool call that hangs.
        Assert.Equal(4, setup.Events.Of<QueryErrorEvent>().Count());
        Assert.Equal(3, setup.Executor.Calls.Count);
    }

    [Fact]
    public async Task Refusals_count_towards_the_same_limit_and_a_success_resets_it()
    {
        var failing = true;
        var setup = Create(run: (sql, user, ct) => failing ? Fail(sql, user, ct) : Task.FromResult(FakeQueryExecutor.Rows(1)));

        await setup.RunSql("DROP TABLE x"); // refused
        await setup.RunSql(ValidSql); // database error
        failing = false;
        await setup.RunSql(ValidSql); // success: the count starts over
        failing = true;
        await setup.RunSql(ValidSql);
        var afterOneFailure = await setup.RunSql(ValidSql);

        Assert.DoesNotContain("STOP", afterOneFailure);
    }

    [Fact]
    public async Task The_sql_time_of_a_turn_is_capped()
    {
        var setup = Create(
            new AgentOptions { MaxSqlTimePerTurn = TimeSpan.FromMilliseconds(40) },
            async (_, _, _) =>
            {
                await Task.Delay(60);
                return FakeQueryExecutor.Rows(1);
            });

        await setup.RunSql(ValidSql);
        var second = await setup.RunSql(ValidSql);

        Assert.Equal(SqlAgentTools.SqlTimeExhaustedMessage, second);
        Assert.Single(setup.Executor.Calls);
    }

    [Fact]
    public async Task list_tables_and_describe_tables_use_the_callers_role()
    {
        var setup = Create(user: Rep7);

        var list = ((JsonElement)(await setup.Tool("list_tables").InvokeAsync())!).GetString()!;
        var describe = ((JsonElement)(await setup.Tool("describe_tables").InvokeAsync(
            new AIFunctionArguments { ["names"] = new[] { "Sales.SalesOrderHeader", "Sales.SalesTerritory" } }))!).GetString()!;

        Assert.Contains("Sales.SalesOrderHeader", list);
        Assert.DoesNotContain("SalesTerritory", list);
        Assert.Contains("Id int", describe);
        Assert.Contains("'Sales.SalesTerritory' was not found or is not available", describe);
        Assert.Equal(["list_tables", "describe_tables"], setup.Events.Of<ToolCallEvent>().Select(e => e.Tool));
    }
}
