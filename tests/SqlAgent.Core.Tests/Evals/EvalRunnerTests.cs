using SqlAgent.Cli.Evals;
using SqlAgent.Core.Agent;
using SqlAgent.Core.Execution;
using SqlAgent.Core.Governance;
using SqlAgent.Core.Guardrails;
using SqlAgent.Core.Tests.Agent.Fakes;
using static SqlAgent.Core.Tests.Agent.Fakes.ScriptedChatClient;

namespace SqlAgent.Core.Tests.Evals;

public class EvalRunnerTests
{
    private const string ReferenceSql = "SELECT COUNT(*) AS Orders FROM Sales.SalesOrderHeader";

    private static readonly GoldenItem Item = new(
        "count", "demo-finance", "How many orders are there?", ReferenceSql, GoldenSetLoader.SimpleTier, Ordered: false, Holdout: false);

    private static QueryResult Count(int value) => new([new QueryColumn("n", "int")], [[value]], false, 1);

    /// <summary>The first query is always the reference; the ones after it are the agent's.</summary>
    private static FakeQueryExecutor Executor(QueryResult reference, QueryResult? agent = null)
    {
        var calls = 0;
        return new FakeQueryExecutor((_, _, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1 ? reference : agent ?? reference));
    }

    private static async Task<ItemResult> RunAsync(ScriptedChatClient model, IQueryExecutor executor, GoldenItem? item = null, int maxIterations = 6)
    {
        var llm = new LlmOptions { MaxIterations = maxIterations };
        var catalog = new StubSchemaCatalog();
        var guardrail = new SqlGuardrail();
        var registry = new RoleAgentRegistry(ChatClientFactory.Wrap(model, llm), catalog, "SYSTEM PROMPT", llm);
        var turns = new AgentTurnRunner(registry, catalog, guardrail, executor, NullAuditSink.Instance, new AgentOptions());
        using var runner = new EvalRunner(turns, catalog, guardrail, executor, maxIterations);
        return await runner.RunAsync(item ?? Item);
    }

    [Fact]
    public async Task A_query_that_returns_the_reference_result_passes_and_the_run_is_measured()
    {
        var model = new ScriptedChatClient(RunSql("SELECT COUNT(*) AS Total FROM Sales.SalesOrderHeader"), Reply("There are 5 orders.", 100, 20));

        var result = await RunAsync(model, Executor(Count(5)));

        Assert.Equal(ItemVerdict.Pass, result.Verdict);
        Assert.Equal(2, result.Iterations); // one model call to ask for the query, one to answer
        Assert.Equal(1, result.QueryCount);
        Assert.Equal(0, result.GuardrailBlocks);
        Assert.Equal(110, result.InputTokens);
        Assert.Equal(25, result.OutputTokens);
        Assert.Contains("Sales.SalesOrderHeader", result.AgentSql);
        Assert.Equal("There are 5 orders.", result.Answer);
        Assert.False(result.GuardrailFalsePositive);
    }

    [Fact]
    public async Task The_last_successful_query_is_the_one_scored()
    {
        // The first query is wrong (the reference says 5), the second is right: the answer rests on the second.
        var calls = 0;
        var executor = new FakeQueryExecutor((_, _, _) => Task.FromResult(Interlocked.Increment(ref calls) switch
        {
            1 => Count(5),   // reference
            2 => Count(99),  // the agent's first try
            _ => Count(5),   // its correction
        }));
        var model = new ScriptedChatClient(
            RunSql("SELECT COUNT(*) AS Total FROM Sales.SalesOrderHeader WHERE 1 = 1"),
            RunSql("SELECT COUNT(*) AS Total FROM Sales.SalesOrderHeader"),
            Reply("There are 5 orders."));

        var result = await RunAsync(model, executor);

        Assert.Equal(ItemVerdict.Pass, result.Verdict);
        Assert.Equal(2, result.QueryCount);
    }

    [Fact]
    public async Task A_query_with_other_values_is_a_wrong_result_with_the_reason()
    {
        var model = new ScriptedChatClient(RunSql("SELECT COUNT(*) AS Total FROM Sales.SalesOrderHeader"), Reply("There are 7 orders."));

        var result = await RunAsync(model, Executor(Count(5), Count(7)));

        Assert.Equal(ItemVerdict.WrongResult, result.Verdict);
        Assert.NotEmpty(result.Detail);
    }

    [Fact]
    public async Task An_answer_with_no_successful_query_is_scored_as_no_query()
    {
        var result = await RunAsync(new ScriptedChatClient(Reply("I am not able to say.")), Executor(Count(5)));

        Assert.Equal(ItemVerdict.NoQuery, result.Verdict);
        Assert.Equal(1, result.Iterations);
        Assert.Null(result.AgentSql);
    }

    [Fact]
    public async Task A_refused_query_is_counted_and_the_item_fails_without_one()
    {
        var model = new ScriptedChatClient(
            RunSql("SELECT * FROM Sales.SalesOrderHeader"),
            Reply("I could not run that."));

        var result = await RunAsync(model, Executor(Count(5)));

        Assert.Equal(1, result.GuardrailBlocks);
        Assert.Equal(ItemVerdict.NoQuery, result.Verdict);
    }

    [Fact]
    public async Task A_tool_loop_stopped_by_the_cap_is_a_fallback_and_an_iteration_cap_hit()
    {
        var model = new ScriptedChatClient(Call("list_tables"), Call("list_tables"), Call("list_tables"), Call("list_tables"));

        var result = await RunAsync(model, Executor(Count(5)), maxIterations: 2);

        Assert.Equal(ItemVerdict.Fallback, result.Verdict);
        Assert.True(result.IterationCapHit);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task A_failing_model_call_is_a_model_error()
    {
        var result = await RunAsync(new ScriptedChatClient(Throw(new HttpRequestException("down"))), Executor(Count(5)));

        Assert.Equal(ItemVerdict.ModelError, result.Verdict);
    }

    [Fact]
    public async Task A_reference_that_fails_is_reported_as_broken_and_the_model_is_never_asked()
    {
        var executor = new FakeQueryExecutor((_, _, _) => throw new QueryExecutionException(QueryFailure.InvalidQuery, "Invalid column name 'X'."));

        var result = await RunAsync(new ScriptedChatClient(), executor);

        Assert.Equal(ItemVerdict.ReferenceError, result.Verdict);
        Assert.Contains("Invalid column name", result.Detail);
    }

    [Fact]
    public async Task A_reference_with_no_rows_is_broken_because_it_cannot_tell_right_from_wrong()
    {
        var empty = new QueryResult([new QueryColumn("n", "int")], [], false, 1);

        var result = await RunAsync(new ScriptedChatClient(), Executor(empty));

        Assert.Equal(ItemVerdict.ReferenceError, result.Verdict);
    }

    [Fact]
    public async Task A_reference_the_guardrail_refuses_is_a_false_positive_but_is_still_scored()
    {
        // A wildcard is refused by the guardrail, though the reference itself is a perfectly legitimate query.
        var item = Item with { ReferenceSql = "SELECT * FROM Sales.SalesOrderHeader" };
        var model = new ScriptedChatClient(RunSql("SELECT COUNT(*) AS Total FROM Sales.SalesOrderHeader"), Reply("There are 5 orders."));

        var result = await RunAsync(model, Executor(Count(5)), item);

        Assert.True(result.GuardrailFalsePositive);
        Assert.Equal(ItemVerdict.Pass, result.Verdict);
    }

    [Fact]
    public async Task Each_item_gets_its_own_trace_and_model_call_count()
    {
        var model = new ScriptedChatClient(
            RunSql("SELECT COUNT(*) AS Total FROM Sales.SalesOrderHeader"), Reply("There are 5 orders."),
            Reply("Nothing to report."));
        var llm = new LlmOptions();
        var catalog = new StubSchemaCatalog();
        var executor = Executor(Count(5));
        var guardrail = new SqlGuardrail();
        var registry = new RoleAgentRegistry(ChatClientFactory.Wrap(model, llm), catalog, "SYSTEM PROMPT", llm);
        var turns = new AgentTurnRunner(registry, catalog, guardrail, executor, NullAuditSink.Instance, new AgentOptions());
        using var runner = new EvalRunner(turns, catalog, guardrail, executor, llm.MaxIterations);

        var first = await runner.RunAsync(Item);
        var second = await runner.RunAsync(Item with { Id = "again" });

        Assert.NotEqual(first.TraceId, second.TraceId);
        Assert.Equal(2, first.Iterations);
        Assert.Equal(1, second.Iterations);
    }
}
