using Microsoft.Extensions.AI;
using SqlAgent.Core.Agent;
using SqlAgent.Core.Execution;
using SqlAgent.Core.Guardrails;
using SqlAgent.Core.Tests.Agent.Fakes;
using static SqlAgent.Core.Tests.Agent.Fakes.ScriptedChatClient;

namespace SqlAgent.Core.Tests.Agent;

public class AgentTurnRunnerTests
{
    private const string CountSql = "SELECT COUNT(*) AS Orders FROM Sales.SalesOrderHeader";

    private sealed class Harness
    {
        public Harness(ScriptedChatClient model, LlmOptions? llm = null, AgentOptions? options = null, FakeQueryExecutor? executor = null)
        {
            Model = model;
            Executor = executor ?? new FakeQueryExecutor();
            var catalog = new StubSchemaCatalog();
            var chat = ChatClientFactory.Wrap(model, llm ?? new LlmOptions());
            Registry = new RoleAgentRegistry(chat, catalog, "SYSTEM PROMPT", llm ?? new LlmOptions());
            Runner = new AgentTurnRunner(Registry, catalog, new SqlGuardrail(), Executor, Audit, options ?? new AgentOptions());
        }

        public ScriptedChatClient Model { get; }
        public FakeQueryExecutor Executor { get; }
        public RoleAgentRegistry Registry { get; }
        public AgentTurnRunner Runner { get; }
        public RecordingAuditSink Audit { get; } = new();
        public ListEventSink Events { get; private set; } = new();
        public ConversationStore Store { get; } = new();

        public ConversationLease NewConversation(UserContext user, string sub = "alice") => Store.Create(sub, user.Role);

        public async Task<TurnOutcome> Run(string message, ConversationLease lease, UserContext? user = null, CancellationToken ct = default)
        {
            Events = new ListEventSink();
            return await Runner.RunAsync(new TurnRequest("alice", user ?? Finance, message, "trace1"), lease, Events, ct);
        }
    }

    private static readonly UserContext Finance = new(Role.Finance);

    private static QueryResult OrderCount(long count) =>
        new([new QueryColumn("Orders", "int")], [[(int)count]], false, 2);

    [Fact]
    public async Task A_normal_turn_explores_queries_answers_and_is_saved()
    {
        var model = new ScriptedChatClient(
            Call("list_tables"),
            Call("describe_tables", new() { ["names"] = new[] { "Sales.SalesOrderHeader" } }),
            RunSql(CountSql, "count orders", callId: "call-sql"),
            Reply("There are 31465 orders.", inputTokens: 100, outputTokens: 20));
        var h = new Harness(model, executor: new FakeQueryExecutor((_, _, _) => Task.FromResult(OrderCount(31465))));
        using var lease = h.NewConversation(Finance);

        var outcome = await h.Run("How many orders are there?", lease);

        Assert.Equal(TurnOutcome.Completed, outcome);
        Assert.Equal(["tool_call", "tool_call", "tool_call", "query_result", "text", "usage"],
            h.Events.Events.Select(e => e.EventName));
        Assert.Equal("call-sql", h.Events.Of<QueryResultEvent>().Single().CallId);
        Assert.Equal("There are 31465 orders.", h.Events.Of<TextEvent>().Single().Delta);
        Assert.Equal(new UsageEvent(100 + 3 * 10, 20 + 3 * 5), h.Events.Of<UsageEvent>().Single());
        Assert.NotNull(lease.Snapshot);
    }

    [Fact]
    public async Task Text_the_model_says_before_a_tool_call_is_not_part_of_the_answer()
    {
        var model = new ScriptedChatClient(
            Call("list_tables", narration: "Let me look at the tables first."),
            RunSql(CountSql),
            Reply("There are 3 orders."));
        var h = new Harness(model);
        using var lease = h.NewConversation(Finance);

        await h.Run("How many orders?", lease);

        Assert.Equal("There are 3 orders.", Assert.Single(h.Events.Of<TextEvent>()).Delta);
    }

    [Fact]
    public async Task Tools_are_passed_per_run_and_the_agent_holds_the_role_examples()
    {
        var model = new ScriptedChatClient(Reply("Hello. What would you like to know?"));
        var h = new Harness(model);
        using var lease = h.NewConversation(new UserContext(Role.SalesRep, 3), "rep");

        await h.Run("hi", lease, new UserContext(Role.SalesRep, 3));

        var (_, options) = Assert.Single(model.Calls);
        Assert.Equal(["describe_tables", "list_tables", "run_sql"], options!.Tools!.Select(t => t.Name).Order());
        Assert.Contains("SYSTEM PROMPT", options.Instructions);
        Assert.Contains("How many orders are there?", options.Instructions);
        Assert.Equal(0f, options.Temperature);
    }

    [Fact]
    public async Task A_follow_up_turn_continues_the_same_session()
    {
        var model = new ScriptedChatClient(
            RunSql(CountSql), Reply("There are 3 orders."),
            Reply("Which year?"));
        var h = new Harness(model);
        using var lease = h.NewConversation(Finance);
        await h.Run("How many orders?", lease);

        await h.Run("And last year?", lease);

        var secondTurnMessages = model.Calls[^1].Messages;
        Assert.Contains(secondTurnMessages, m => m.Role == ChatRole.User && m.Text == "How many orders?");
        Assert.Contains(secondTurnMessages, m => m.Role == ChatRole.Assistant && m.Text == "There are 3 orders.");
        Assert.Contains(secondTurnMessages, m => m.Role == ChatRole.User && m.Text == "And last year?");
    }

    [Fact]
    public async Task Hitting_the_iteration_cap_gives_the_fixed_message_and_saves_nothing()
    {
        var model = new ScriptedChatClient(RunSql(CountSql), RunSql(CountSql), RunSql(CountSql), RunSql(CountSql));
        var h = new Harness(model, new LlmOptions { MaxIterations = 2 });
        using var lease = h.NewConversation(Finance);

        var outcome = await h.Run("How many orders?", lease);

        Assert.Equal(TurnOutcome.FellBack, outcome);
        Assert.Equal(AgentTurnRunner.FallbackMessage, Assert.Single(h.Events.Of<TextEvent>()).Delta);
        Assert.Null(lease.Snapshot);
    }

    [Fact]
    public async Task An_answer_with_figures_but_no_successful_query_is_replaced()
    {
        var model = new ScriptedChatClient(Reply("There were about 31000 orders last year."));
        var h = new Harness(model);
        using var lease = h.NewConversation(Finance);

        var outcome = await h.Run("How many orders last year?", lease);

        Assert.Equal(TurnOutcome.FellBack, outcome);
        Assert.Equal(AgentTurnRunner.FallbackMessage, Assert.Single(h.Events.Of<TextEvent>()).Delta);
    }

    [Fact]
    public async Task An_answer_after_only_failed_queries_is_replaced_when_it_states_figures()
    {
        var model = new ScriptedChatClient(RunSql(CountSql), Reply("I guess it is 5000."));
        var h = new Harness(model, executor: new FakeQueryExecutor((_, _, _) =>
            throw new QueryExecutionException(QueryFailure.InvalidQuery, "Invalid column name 'x'.")));
        using var lease = h.NewConversation(Finance);

        var outcome = await h.Run("How many?", lease);

        Assert.Equal(TurnOutcome.FellBack, outcome);
        Assert.Single(h.Events.Of<QueryErrorEvent>());
    }

    [Fact]
    public async Task A_reply_without_figures_needs_no_data()
    {
        var model = new ScriptedChatClient(Reply("Which year do you mean?"));
        var h = new Harness(model);
        using var lease = h.NewConversation(Finance);

        var outcome = await h.Run("How many orders?", lease);

        Assert.Equal(TurnOutcome.Completed, outcome);
        Assert.Equal("Which year do you mean?", Assert.Single(h.Events.Of<TextEvent>()).Delta);
    }

    [Fact]
    public async Task A_figure_that_is_in_no_result_gets_a_warning_note_and_a_metric()
    {
        var model = new ScriptedChatClient(RunSql(CountSql), Reply("There are 31465 orders, up from 28000."));
        var h = new Harness(model, executor: new FakeQueryExecutor((_, _, _) => Task.FromResult(OrderCount(31465))));
        using var lease = h.NewConversation(Finance);

        var outcome = await h.Run("How many orders?", lease);

        Assert.Equal(TurnOutcome.Completed, outcome);
        var texts = h.Events.Of<TextEvent>().Select(e => e.Delta).ToList();
        Assert.Equal(2, texts.Count);
        Assert.Equal(AgentTurnRunner.UngroundedFiguresNote, texts[1]);
        Assert.Contains("could not be verified", texts[1]);
    }

    [Fact]
    public async Task Grounded_figures_get_no_note()
    {
        var model = new ScriptedChatClient(RunSql(CountSql), Reply("There are 31,465 orders."));
        var h = new Harness(model, executor: new FakeQueryExecutor((_, _, _) => Task.FromResult(OrderCount(31465))));
        using var lease = h.NewConversation(Finance);

        await h.Run("How many orders?", lease);

        Assert.Single(h.Events.Of<TextEvent>());
    }

    [Fact]
    public async Task A_refused_query_is_reported_and_the_model_can_explain()
    {
        var model = new ScriptedChatClient(
            RunSql("SELECT * FROM HumanResources.EmployeePayHistory"),
            Reply("I can't read pay rates; they are outside the tables available to you."));
        var h = new Harness(model);
        using var lease = h.NewConversation(Finance);

        var outcome = await h.Run("Show employee pay rates", lease);

        Assert.Equal(TurnOutcome.Completed, outcome);
        Assert.Single(h.Events.Of<GuardrailBlockedEvent>());
        Assert.Empty(h.Executor.Calls);
        Assert.Contains("can't read pay rates", h.Events.Of<TextEvent>().Single().Delta);
    }

    [Fact]
    public async Task A_cancelled_turn_sends_nothing_more_and_is_not_saved()
    {
        var model = new ScriptedChatClient(RunSql(CountSql), Hang());
        var h = new Harness(model);
        using var lease = h.NewConversation(Finance);
        using var cts = new CancellationTokenSource();

        var run = h.Run("How many orders?", lease, ct: cts.Token);
        while (h.Events.Events.Count < 2) await Task.Delay(10); // tool_call and query_result are out; the model is "thinking"
        cts.Cancel();
        var outcome = await run;

        Assert.Equal(TurnOutcome.Cancelled, outcome);
        Assert.Empty(h.Events.Of<TextEvent>());
        Assert.Empty(h.Events.Of<ErrorEvent>());
        Assert.Null(lease.Snapshot);
    }

    [Fact]
    public async Task A_failed_model_call_sends_a_generic_error_and_is_not_saved()
    {
        var model = new ScriptedChatClient(Throw(new HttpRequestException("connection refused: 10.0.0.5:11434")));
        var h = new Harness(model);
        using var lease = h.NewConversation(Finance);

        var outcome = await h.Run("How many orders?", lease);

        Assert.Equal(TurnOutcome.Failed, outcome);
        var error = Assert.Single(h.Events.Of<ErrorEvent>());
        Assert.Equal(AgentTurnRunner.ModelFailedMessage, error.Message);
        Assert.DoesNotContain("10.0.0.5", error.Message);
        Assert.Null(lease.Snapshot);
    }

    [Fact]
    public async Task A_failed_follow_up_leaves_the_earlier_turns_intact()
    {
        var model = new ScriptedChatClient(
            Reply("Which year do you mean?"),
            Throw(new HttpRequestException("down")),
            Reply("Thanks, noted."));
        var h = new Harness(model);
        using var lease = h.NewConversation(Finance);
        await h.Run("How many orders?", lease);
        var saved = lease.Snapshot!.Value.GetRawText();

        await h.Run("2013", lease);

        Assert.Equal(saved, lease.Snapshot!.Value.GetRawText());
        await h.Run("2014", lease);
        var lastMessages = model.Calls[^1].Messages;
        Assert.DoesNotContain(lastMessages, m => m.Text == "2013");
        Assert.Contains(lastMessages, m => m.Text == "How many orders?");
    }

    [Fact]
    public void Instructions_hold_the_prompt_and_the_examples_of_the_role_only()
    {
        var text = RoleAgentRegistry.BuildInstructions("PROMPT", Role.Finance, new StubSchemaCatalog().GetExamples(Role.Finance));

        Assert.StartsWith("PROMPT", text);
        Assert.Contains("SELECT COUNT(*) AS Orders FROM Sales.SalesOrderHeader", text);
    }
}
