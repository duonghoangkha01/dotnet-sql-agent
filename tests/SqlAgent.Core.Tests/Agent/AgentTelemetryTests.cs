using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using SqlAgent.Core.Agent;
using SqlAgent.Core.Execution;
using SqlAgent.Core.Governance;
using SqlAgent.Core.Guardrails;
using SqlAgent.Core.Tests.Agent.Fakes;
using static SqlAgent.Core.Tests.Agent.Fakes.ScriptedChatClient;

namespace SqlAgent.Core.Tests.Agent;

/// <summary>
/// The custom spans and metrics. Meters are process-wide and other tests run in parallel, so the metric checks look for the
/// measurements this test made (a unique model name, a unique trace) and never count on totals.
/// </summary>
public class AgentTelemetryTests
{
    private const string CountSql = "SELECT COUNT(*) AS Orders FROM Sales.SalesOrderHeader";

    private static readonly UserContext Finance = new(Role.Finance);

    private sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        public ConcurrentBag<Measurement> Measurements { get; } = [];

        public Recorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == AgentTelemetry.MeterName) listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Measurements.Add(new(i.Name, v, ToDictionary(tags))));
            _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Measurements.Add(new(i.Name, v, ToDictionary(tags))));
            _listener.Start();
        }

        public IEnumerable<Measurement> Of(string instrument) => Measurements.Where(m => m.Instrument == instrument);

        public void Dispose() => _listener.Dispose();

        private static IReadOnlyDictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var result = new Dictionary<string, object?>();
            foreach (var tag in tags) result[tag.Key] = tag.Value;
            return result;
        }
    }

    /// <summary>Records the spans of one trace: the test starts a root span of its own and keeps only its descendants.</summary>
    private sealed class SpanRecorder : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly Activity _root;
        public ConcurrentBag<Activity> Spans { get; } = [];

        public SpanRecorder()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name is AgentTelemetry.SourceName or ChatClientFactory.TelemetrySourceName or "test-root",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = a => { if (a.TraceId == _root!.TraceId && a != _root) Spans.Add(a); },
            };
            ActivitySource.AddActivityListener(_listener);
            _root = new ActivitySource("test-root").StartActivity("root")!;
        }

        public string TraceId => _root.TraceId.ToString();

        public void Dispose()
        {
            _root.Dispose();
            _listener.Dispose();
        }
    }

    private static object? Tag(Activity span, string key) => span.GetTagItem(key);

    [Fact]
    public void An_allowed_validation_is_one_span_with_the_verdict_and_no_sql_text()
    {
        using var spans = new SpanRecorder();

        new SqlGuardrail().Validate(CountSql, new StubSchemaCatalog().GetAllowList(Role.Finance));

        var span = Assert.Single(spans.Spans, s => s.OperationName == "guardrail.validate");
        Assert.Equal(true, Tag(span, "allowed"));
        Assert.Equal("", Tag(span, "violation_codes"));
        Assert.DoesNotContain(span.TagObjects, t => t.Value?.ToString()?.Contains("SalesOrderHeader") == true);
    }

    [Fact]
    public void A_refusal_records_each_violation_code_once()
    {
        using var spans = new SpanRecorder();

        new SqlGuardrail().Validate("SELECT * FROM Person.Person; DROP TABLE Sales.SalesOrderHeader", new StubSchemaCatalog().GetAllowList(Role.Finance));
        new SqlGuardrail().Validate("SELECT 1 FROM Person.Person UNION SELECT 1 FROM Person.Person", new StubSchemaCatalog().GetAllowList(Role.SalesRep));

        var refused = spans.Spans.Where(s => s.OperationName == "guardrail.validate" && Equals(Tag(s, "allowed"), false)).ToList();
        Assert.Equal(2, refused.Count);
        Assert.Contains(refused, s => Tag(s, "violation_codes")?.ToString()!.Contains(nameof(ViolationCode.MultipleStatements)) == true);
        Assert.Contains(refused, s => Tag(s, "violation_codes")?.ToString()!.Contains(nameof(ViolationCode.TableNotAllowed)) == true);
        Assert.All(refused, s => Assert.DoesNotContain("Person.Person", string.Join(' ', s.TagObjects.Select(t => t.Value))));
    }

    [Fact]
    public async Task A_turn_records_tokens_by_model_and_direction_and_how_long_it_took()
    {
        using var metrics = new Recorder();
        var llm = new LlmOptions();
        var catalog = new StubSchemaCatalog();
        var model = new ScriptedChatClient(RunSql(CountSql), Reply("There are 3 orders.", inputTokens: 100, outputTokens: 20));
        var registry = new RoleAgentRegistry(ChatClientFactory.Wrap(model, llm), catalog, "SYSTEM PROMPT", llm);
        var runner = new AgentTurnRunner(registry, catalog, new SqlGuardrail(), new FakeQueryExecutor(), NullAuditSink.Instance,
            new AgentOptions { ModelName = "telemetry-test-model" });
        using var store = new ConversationStore();
        using var lease = store.Create("alice", Role.Finance);

        await runner.RunAsync(new TurnRequest("alice", Finance, "How many orders?", "trace"), lease, new ListEventSink(), CancellationToken.None);

        var tokens = metrics.Of("sqlagent.tokens").Where(m => Equals(m.Tags["model"], "telemetry-test-model")).ToList();
        Assert.Equal(110, tokens.Single(m => Equals(m.Tags["direction"], "input")).Value);
        Assert.Equal(25, tokens.Single(m => Equals(m.Tags["direction"], "output")).Value);
        Assert.Contains(metrics.Of("sqlagent.turn.duration"), m => Equals(m.Tags["role"], "finance") && Equals(m.Tags["outcome"], nameof(TurnOutcome.Completed)) && m.Value >= 0);
    }

    [Fact]
    public async Task A_refused_query_counts_a_guardrail_block_per_code_and_a_cut_short_loop_counts_a_cap_hit()
    {
        using var metrics = new Recorder();
        var llm = new LlmOptions { MaxIterations = 2 };
        var catalog = new StubSchemaCatalog();
        var model = new ScriptedChatClient(
            RunSql("SELECT * FROM Sales.SalesOrderHeader"), Call("list_tables"), Call("list_tables"), Call("list_tables"));
        var registry = new RoleAgentRegistry(ChatClientFactory.Wrap(model, llm), catalog, "SYSTEM PROMPT", llm);
        var runner = new AgentTurnRunner(registry, catalog, new SqlGuardrail(), new FakeQueryExecutor(), NullAuditSink.Instance, new AgentOptions());
        using var store = new ConversationStore();
        using var lease = store.Create("alice", Role.Finance);

        var outcome = await runner.RunAsync(new TurnRequest("alice", Finance, "How many orders?", "trace"), lease, new ListEventSink(), CancellationToken.None);

        Assert.Equal(TurnOutcome.FellBack, outcome);
        Assert.Contains(metrics.Of("sqlagent.guardrail.blocks"), m => Equals(m.Tags["code"], nameof(ViolationCode.WildcardNotAllowed)) && m.Value == 1);
        Assert.Contains(metrics.Of("sqlagent.iteration_cap_hits"), m => m.Value == 1);
    }

    [Fact]
    public void There_is_no_cost_instrument()
    {
        // Cost is computed once, in the eval report, from a dated list price. Nothing running accrues it.
        var names = typeof(AgentTelemetry).GetFields().Select(f => f.Name);
        Assert.DoesNotContain(names, n => n.Contains("cost", StringComparison.OrdinalIgnoreCase));
    }
}
