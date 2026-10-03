using System.Collections.Concurrent;
using System.Diagnostics;
using SqlAgent.Core;
using SqlAgent.Core.Agent;
using SqlAgent.Core.Evals;
using SqlAgent.Core.Execution;
using SqlAgent.Core.Guardrails;
using SqlAgent.Core.Schema;

namespace SqlAgent.Cli.Evals;

public enum ItemVerdict
{
    Pass,

    /// <summary>The scored query ran but returned something other than the reference result.</summary>
    WrongResult,

    /// <summary>The turn was answered without any query succeeding.</summary>
    NoQuery,

    /// <summary>The turn ended in the deterministic fallback message (tool loop cut short, or an answer without data).</summary>
    Fallback,

    /// <summary>The model call failed.</summary>
    ModelError,

    /// <summary>The reference query itself failed or does not fit the row cap: the golden item is broken, and it is left out of the accuracy.</summary>
    ReferenceError,
}

/// <summary>What one golden item came to. No row values are kept: only the SQL, counts and the reason for a failure.</summary>
public sealed record ItemResult(
    string Id,
    string Persona,
    string Tier,
    bool Holdout,
    string Question,
    ItemVerdict Verdict,
    string Detail,
    int Iterations,
    int QueryCount,
    int GuardrailBlocks,
    int QueryErrors,
    bool GuardrailFalsePositive,
    bool IterationCapHit,
    long InputTokens,
    long OutputTokens,
    long LatencyMs,
    string? AgentSql,
    string? Answer,
    string TraceId)
{
    public bool Passed => Verdict == ItemVerdict.Pass;
}

/// <summary>Keeps every event of a turn, in order.</summary>
public sealed class CapturingEventSink : IEventSink
{
    private readonly List<SseEvent> _events = [];

    public IReadOnlyList<SseEvent> Events
    {
        get { lock (_events) return _events.ToList(); }
    }

    public ValueTask EmitAsync(SseEvent evt, CancellationToken ct = default)
    {
        lock (_events) _events.Add(evt);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Runs golden items through the same agent the API runs (<see cref="AgentTurnRunner"/>, the shared prompt, tools and
/// semantic layer), with no audit trail, and scores each by execution accuracy: the last query that ran successfully
/// before the answer must return what the reference query returns (<see cref="ResultSetComparer"/>). A turn that ends in
/// the fallback message or an error fails whatever queries ran. Run items one at a time: the number of model calls is
/// read from the item's own trace.
/// </summary>
public sealed class EvalRunner : IDisposable
{
    private readonly AgentTurnRunner _turns;
    private readonly ISchemaCatalog _catalog;
    private readonly SqlGuardrail _guardrail;
    private readonly IQueryExecutor _executor;
    private readonly int _maxIterations;
    private readonly ConcurrentDictionary<string, int> _modelCalls = new();
    private readonly ActivityListener _listener;

    /// <param name="guardrail">Built with the same limits as <paramref name="executor"/>, so its row cap and the executor's agree.</param>
    public EvalRunner(
        AgentTurnRunner turns,
        ISchemaCatalog catalog,
        SqlGuardrail guardrail,
        IQueryExecutor executor,
        int maxIterations)
    {
        _turns = turns;
        _catalog = catalog;
        _guardrail = guardrail;
        _executor = executor;
        _maxIterations = maxIterations;

        // Each "chat" span of the model client is one model call.
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentTelemetry.SourceName || source.Name == ChatClientFactory.TelemetrySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.Source.Name == ChatClientFactory.TelemetrySourceName && activity.OperationName.StartsWith("chat", StringComparison.Ordinal))
                    _modelCalls.AddOrUpdate(activity.TraceId.ToString(), 1, (_, n) => n + 1);
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    public async Task<ItemResult> RunAsync(GoldenItem item, CancellationToken ct = default)
    {
        using var span = AgentTelemetry.Source.StartActivity("eval.item");
        span?.SetTag("item", item.Id);
        var traceId = span?.TraceId.ToString() ?? ActivityTraceId.CreateRandom().ToString();

        var user = item.User;
        var allow = _catalog.GetAllowList(user.Role);

        // Whether the guardrail would refuse the known-good reference query: its false-positive rate on legitimate SQL.
        var falsePositive = !_guardrail.Validate(item.ReferenceSql, allow).Allowed;

        QueryResult reference;
        try
        {
            // As the persona's own database user, so row-level security and the grants apply to the reference too.
            reference = await _executor.ExecuteAsync(item.ReferenceSql, user, ct);
        }
        catch (QueryExecutionException ex)
        {
            return Broken(item, $"reference query failed: {ex.Message}", falsePositive, traceId);
        }

        if (reference.Truncated || reference.RowCount == 0)
        {
            var why = reference.Truncated ? $"more than {GoldenSetLoader.MaxReferenceRows} rows" : "no rows";
            return Broken(item, $"reference result has {why}", falsePositive, traceId);
        }

        var sink = new CapturingEventSink();
        var clock = Stopwatch.StartNew();
        using var conversations = new ConversationStore();
        using var lease = conversations.Create("eval", user.Role);
        TurnOutcome outcome;
        try
        {
            outcome = await _turns.RunAsync(new TurnRequest("eval", user, item.Question, traceId), lease, sink, ct);
        }
        catch
        {
            _modelCalls.TryRemove(traceId, out _);
            throw;
        }

        clock.Stop();
        ct.ThrowIfCancellationRequested();

        return Score(item, reference, outcome, sink.Events, clock.ElapsedMilliseconds, falsePositive, traceId);
    }

    private ItemResult Score(
        GoldenItem item, QueryResult reference, TurnOutcome outcome, IReadOnlyList<SseEvent> events,
        long latencyMs, bool falsePositive, string traceId)
    {
        _modelCalls.TryRemove(traceId, out var iterations);
        var usage = events.OfType<UsageEvent>().LastOrDefault();
        var scored = events.OfType<QueryResultEvent>().LastOrDefault();
        var answer = string.Concat(events.OfType<TextEvent>().Select(e => e.Delta));
        var capHit = outcome == TurnOutcome.FellBack && iterations >= _maxIterations;

        var (verdict, detail) = outcome switch
        {
            TurnOutcome.Failed => (ItemVerdict.ModelError, events.OfType<ErrorEvent>().LastOrDefault()?.Message ?? "the model run failed"),
            TurnOutcome.FellBack => (ItemVerdict.Fallback, capHit ? "iteration cap reached" : "fallback answer"),
            _ when scored is null => (ItemVerdict.NoQuery, "answered without a successful query"),
            _ when scored.Truncated => (ItemVerdict.WrongResult, "the agent's result is larger than the eval row cap"),
            _ => Compare(reference, scored, item.Ordered),
        };

        return new ItemResult(
            item.Id, item.Persona, item.Tier, item.Holdout, item.Question, verdict, detail,
            iterations,
            QueryCount: events.OfType<ToolCallEvent>().Count(e => e.Tool == "run_sql"),
            GuardrailBlocks: events.OfType<GuardrailBlockedEvent>().Count(),
            QueryErrors: events.OfType<QueryErrorEvent>().Count(),
            falsePositive, capHit,
            usage?.InputTokens ?? 0, usage?.OutputTokens ?? 0, latencyMs,
            scored?.Sql, answer.Length == 0 ? null : answer, traceId);
    }

    private static (ItemVerdict, string) Compare(QueryResult reference, QueryResultEvent scored, bool ordered)
    {
        var actual = new QueryResult(scored.Columns, scored.Rows, scored.Truncated, scored.ElapsedMs);
        var comparison = ResultSetComparer.Compare(reference, actual, ordered);
        return comparison.Match ? (ItemVerdict.Pass, "") : (ItemVerdict.WrongResult, comparison.Reason);
    }

    private static ItemResult Broken(GoldenItem item, string detail, bool falsePositive, string traceId) =>
        new(item.Id, item.Persona, item.Tier, item.Holdout, item.Question, ItemVerdict.ReferenceError, detail,
            Iterations: 0, QueryCount: 0, GuardrailBlocks: 0, QueryErrors: 0, falsePositive, IterationCapHit: false,
            InputTokens: 0, OutputTokens: 0, LatencyMs: 0, AgentSql: null, Answer: null, traceId);
}
