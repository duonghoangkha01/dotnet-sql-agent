using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlAgent.Core.Execution;
using SqlAgent.Core.Governance;
using SqlAgent.Core.Guardrails;
using SqlAgent.Core.Schema;

namespace SqlAgent.Core.Agent;

/// <summary>Who is asking, in which conversation, and what: everything a tool needs that the model must not supply.</summary>
public sealed record TurnContext(UserContext User, string Sub, Guid ConversationId, string Question, string? TraceId);

/// <summary>
/// The three tools of one turn: <c>list_tables</c>, <c>describe_tables</c> and <c>run_sql</c>. A new instance is made
/// per run and bound to the caller's server-side <see cref="TurnContext"/>, so the model can never name a role or a
/// territory: its only inputs are table names, SQL and a purpose. The turn's limits live here too (total SQL time,
/// consecutive failures). Result rows go to the UI through <see cref="IEventSink"/>; what the model reads back is set
/// by <see cref="AgentOptions.ResultVisibility"/>.
/// </summary>
public sealed class SqlAgentTools
{
    public const string StopMessage =
        "STOP: run_sql has failed several times in a row. Do not call it again. Tell the user briefly what went wrong " +
        "and ask them to rephrase or narrow the question.";

    public const string SqlTimeExhaustedMessage =
        "STOP: the time allowed for database queries in this turn is used up. Do not call run_sql again. " +
        "Answer only from the rows you already received, or ask the user to narrow the question.";

    private static readonly JsonSerializerOptions ModelJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // Read by a model, never embedded in HTML: quotes and non-ASCII text stay readable and cost fewer tokens.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TurnContext _turn;
    private readonly ISchemaCatalog _catalog;
    private readonly SqlGuardrail _guardrail;
    private readonly IQueryExecutor _executor;
    private readonly IAuditSink _audit;
    private readonly IEventSink _events;
    private readonly AgentOptions _options;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly List<QueryResult> _results = [];
    private TimeSpan _sqlTime;
    private int _consecutiveFailures;

    public SqlAgentTools(
        TurnContext turn,
        ISchemaCatalog catalog,
        SqlGuardrail guardrail,
        IQueryExecutor executor,
        IAuditSink audit,
        IEventSink events,
        AgentOptions options,
        ILogger? logger = null)
    {
        _turn = turn;
        _catalog = catalog;
        _guardrail = guardrail;
        _executor = executor;
        _audit = audit;
        _events = events;
        _options = options;
        _logger = logger ?? NullLogger.Instance;

        Tools =
        [
            AIFunctionFactory.Create(ListTables, "list_tables",
                "Lists the tables you can query, with a one-line description each. Call this first."),
            AIFunctionFactory.Create(DescribeTables, "describe_tables",
                "Returns the columns, types, foreign keys and join hints of the named tables. Call it before writing SQL."),
            AIFunctionFactory.Create(RunSql, "run_sql",
                "Runs one read-only T-SQL SELECT and returns the result. The full result is shown to the user separately."),
        ];
    }

    /// <summary>
    /// The names, descriptions and parameter schemas of the tools as the model receives them: the part of the prompt that
    /// is the same for every request, for the startup check of the fixed prompt budget.
    /// </summary>
    public static string ToolSchemaText()
    {
        var tools = new SqlAgentTools(new TurnContext(new UserContext(Role.Admin), "", Guid.Empty, "", null),
            null!, null!, null!, null!, null!, new AgentOptions());
        return string.Join('\n', tools.Tools.OfType<AIFunction>().Select(t => $"{t.Name} {t.Description} {t.JsonSchema.GetRawText()}"));
    }

    /// <summary>Pass these to the run as <c>ChatOptions.Tools</c>.</summary>
    public IList<AITool> Tools { get; }

    /// <summary>The results of the queries that ran successfully this turn, in order (for the figure check).</summary>
    public IReadOnlyList<QueryResult> Results
    {
        get { lock (_gate) return _results.ToList(); }
    }

    public bool HadSuccessfulQuery
    {
        get { lock (_gate) return _results.Count > 0; }
    }

    private async Task<string> ListTables()
    {
        await _events.EmitAsync(new ToolCallEvent(CallId(), "list_tables"));
        return PromptBudget.RenderListTables(_catalog.ListTables(_turn.User.Role));
    }

    private async Task<string> DescribeTables([Description("Table names such as Sales.SalesOrderHeader.")] string[] names)
    {
        await _events.EmitAsync(new ToolCallEvent(CallId(), "describe_tables"));
        return SchemaTextRenderer.Render(_catalog.DescribeTables(_turn.User.Role, names ?? []));
    }

    private async Task<string> RunSql(
        [Description("Exactly one T-SQL SELECT statement.")] string sql,
        [Description("One short sentence: what this query is for.")] string purpose,
        CancellationToken ct)
    {
        var callId = CallId();
        sql ??= "";
        await _events.EmitAsync(new ToolCallEvent(callId, "run_sql", sql, purpose), ct);

        // Refused without running: still tell the UI, so the tool_call above is not left without an outcome.
        if (Interlocked.CompareExchange(ref _consecutiveFailures, 0, 0) >= _options.MaxConsecutiveSqlFailures)
        {
            await _events.EmitAsync(new QueryErrorEvent(callId, sql, "Not run: too many failed queries in a row."), ct);
            return StopMessage;
        }

        if (SqlTimeUsed >= _options.MaxSqlTimePerTurn)
        {
            await _events.EmitAsync(new QueryErrorEvent(callId, sql, "Not run: this question has used its database time."), ct);
            return SqlTimeExhaustedMessage;
        }

        var verdict = _guardrail.Validate(sql, _catalog.GetAllowList(_turn.User.Role));
        if (!verdict.Allowed)
        {
            AgentTelemetry.GuardrailBlocks.Add(1);
            await AuditAsync(sql, allowed: false, verdict.Violations, rows: null, elapsedMs: null);
            await _events.EmitAsync(new GuardrailBlockedEvent(callId, sql,
                verdict.Violations.Select(v => new ViolationInfo(v.Code.ToString(), v.Message)).ToList()), ct);
            return Failed(new { blocked = true, problems = verdict.Violations.Select(v => v.Message), hint = "Fix the query and call run_sql again." });
        }

        var executable = verdict.Sql!;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await _executor.ExecuteAsync(executable, _turn.User, ct);
            AddSqlTime(stopwatch.Elapsed);
            lock (_gate) _results.Add(result);
            Interlocked.Exchange(ref _consecutiveFailures, 0);

            await AuditAsync(executable, allowed: true, [], result.RowCount, result.ElapsedMs);
            await _events.EmitAsync(new QueryResultEvent(callId, executable, result.Columns, result.Rows,
                result.RowCount, result.Truncated, result.ElapsedMs), ct);
            return ModelView(result);
        }
        catch (OperationCanceledException)
        {
            // The turn is being discarded, but the query was attempted: keep the record.
            AddSqlTime(stopwatch.Elapsed);
            await AuditAsync(executable, allowed: true, [], rows: null, stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            AddSqlTime(stopwatch.Elapsed);
            // QueryExecutionException messages are written for the model; anything else stays generic.
            var message = ex is QueryExecutionException ? ex.Message : "The query could not be run. Try a simpler query.";
            if (ex is not QueryExecutionException) _logger.LogError(ex, "run_sql failed unexpectedly in conversation {ConversationId}.", _turn.ConversationId);

            await AuditAsync(executable, allowed: true, [], rows: null, stopwatch.ElapsedMilliseconds);
            await _events.EmitAsync(new QueryErrorEvent(callId, executable, message), ct);
            return Failed(new { error = message });
        }
    }

    private TimeSpan SqlTimeUsed
    {
        get { lock (_gate) return _sqlTime; }
    }

    private void AddSqlTime(TimeSpan elapsed)
    {
        lock (_gate) _sqlTime += elapsed;
    }

    /// <summary>Counts a failed or refused call; the call that reaches the limit already carries the stop instruction.</summary>
    private string Failed(object body)
    {
        var failures = Interlocked.Increment(ref _consecutiveFailures);
        var json = JsonSerializer.Serialize(body, ModelJson);
        return failures >= _options.MaxConsecutiveSqlFailures ? json + "\n" + StopMessage : json;
    }

    /// <summary>What the model reads back, limited by <see cref="AgentOptions.ResultVisibility"/>. Always says if the result was cut off.</summary>
    private string ModelView(QueryResult result)
    {
        var columns = result.Columns.Select(c => c.Name).ToList();
        var shown = _options.ResultVisibility switch
        {
            ResultVisibility.None => 0,
            ResultVisibility.Summary => Math.Min(_options.SummaryRows, result.RowCount),
            _ => Math.Min(_options.MaxRowsForModel, result.RowCount),
        };

        return JsonSerializer.Serialize(new
        {
            columns,
            rowCount = result.RowCount,
            truncated = result.Truncated,
            // Only when the model got fewer rows than the query returned, so it never mistakes a sample for everything.
            rowsShown = shown < result.RowCount ? shown : (int?)null,
            rows = shown > 0 ? result.Rows.Take(shown) : null,
        }, ModelJson);
    }

    private ValueTask AuditAsync(string sql, bool allowed, IReadOnlyList<Violation> violations, int? rows, long? elapsedMs) =>
        _audit.WriteAsync(new AuditEntry(_turn.Sub, _turn.User.Role, _turn.ConversationId, _turn.Question, sql,
            allowed, violations, rows, elapsedMs, _turn.TraceId, DateTimeOffset.UtcNow));

    /// <summary>The model's id for this call, so the UI can pair <c>tool_call</c> with its result.</summary>
    private static string CallId() =>
        FunctionInvokingChatClient.CurrentContext?.CallContent.CallId ?? Guid.NewGuid().ToString("N");
}
