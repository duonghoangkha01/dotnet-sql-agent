using System.Diagnostics;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlAgent.Core.Execution;
using SqlAgent.Core.Governance;
using SqlAgent.Core.Guardrails;
using SqlAgent.Core.Schema;

namespace SqlAgent.Core.Agent;

/// <param name="TraceId">The distributed-trace id of the request, recorded with each audited query.</param>
public sealed record TurnRequest(string Sub, UserContext User, string Message, string? TraceId);

public enum TurnOutcome
{
    /// <summary>The model answered; the turn was saved.</summary>
    Completed,

    /// <summary>The answer was replaced by the deterministic fallback message; the turn was not saved.</summary>
    FellBack,

    /// <summary>The model call failed; an <c>error</c> event was sent and the turn was not saved.</summary>
    Failed,

    /// <summary>The caller went away; nothing more was sent and the turn was not saved.</summary>
    Cancelled,
}

/// <summary>
/// Runs one question through a role's agent and turns what happens into events. It is where "never invent a number"
/// is enforced beyond the prompt: an answer given without data, or after the tool loop was cut short, is replaced by a
/// fixed message, and figures that are in no result are flagged. The text is held until the model finishes, because
/// whether it may be shown depends on the whole turn.
/// </summary>
public sealed class AgentTurnRunner(
    RoleAgentRegistry agents,
    ISchemaCatalog catalog,
    SqlGuardrail guardrail,
    IQueryExecutor executor,
    IAuditSink audit,
    AgentOptions options,
    ILogger<AgentTurnRunner>? logger = null)
{
    public const string FallbackMessage = "I couldn't answer this reliably — please rephrase or narrow the question.";

    public const string UngroundedFiguresNote = "\n\n⚠ some figures could not be verified against the results";

    public const string ModelFailedMessage = "The language model could not be reached or failed to answer. Please try again.";

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>Runs the turn on a conversation the caller holds the lease of. Commits to it only when the turn completes.</summary>
    public async Task<TurnOutcome> RunAsync(TurnRequest request, ConversationLease lease, IEventSink sink, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var outcome = await RunCoreAsync(request, lease, sink, ct);
        AgentTelemetry.TurnDuration.Record(clock.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("role", request.User.Role.ToKey()),
            new KeyValuePair<string, object?>("outcome", outcome.ToString()));
        return outcome;
    }

    private async Task<TurnOutcome> RunCoreAsync(TurnRequest request, ConversationLease lease, IEventSink sink, CancellationToken ct)
    {
        var role = request.User.Role;
        var agent = agents.For(role);
        var tools = new SqlAgentTools(
            new TurnContext(request.User, request.Sub, lease.ConversationId, request.Message, request.TraceId),
            catalog, guardrail, executor, audit, sink, options, _logger);

        var text = new StringBuilder();
        var calls = new HashSet<string>();
        var results = new HashSet<string>();
        long inputTokens = 0, outputTokens = 0;
        AgentSession session;

        try
        {
            session = lease.Snapshot is { } snapshot
                ? await agent.DeserializeSessionAsync(snapshot, null, ct)
                : await agent.CreateSessionAsync(ct);

            var runOptions = new ChatClientAgentRunOptions(new ChatOptions { Tools = tools.Tools });
            await foreach (var update in agent.RunStreamingAsync(request.Message, session, runOptions, ct))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent chunk:
                            text.Append(chunk.Text);
                            break;
                        case FunctionCallContent call:
                            // Text before a tool call is the model thinking aloud, not the answer.
                            calls.Add(call.CallId);
                            text.Clear();
                            break;
                        case FunctionResultContent result:
                            results.Add(result.CallId);
                            text.Clear();
                            break;
                        case UsageContent usage:
                            inputTokens += usage.Details.InputTokenCount ?? 0;
                            outputTokens += usage.Details.OutputTokenCount ?? 0;
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return TurnOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The model run failed in conversation {ConversationId}.", lease.ConversationId);
            await sink.EmitAsync(new ErrorEvent(ModelFailedMessage), CancellationToken.None);
            return TurnOutcome.Failed;
        }

        RecordTokens(options.ModelName, inputTokens, outputTokens);
        var answer = text.ToString().Trim();
        var cutShort = calls.Except(results).Any(); // a tool call that never got its result: the iteration cap stopped the loop
        var answeredWithoutData = !tools.HadSuccessfulQuery
                                  && NumberGrounding.FiguresNotInQuestion(answer, request.Message).Count > 0;

        if (cutShort || answer.Length == 0 || answeredWithoutData)
        {
            AgentTelemetry.FallbackAnswers.Add(1);
            if (cutShort) AgentTelemetry.IterationCapHits.Add(1);
            await sink.EmitAsync(new TextEvent(FallbackMessage), ct);
            await EmitUsageAsync(sink, inputTokens, outputTokens, ct);
            return TurnOutcome.FellBack;
        }

        await sink.EmitAsync(new TextEvent(answer), ct);
        if (tools.HadSuccessfulQuery && NumberGrounding.UngroundedFigures(answer, request.Message, tools.Results).Count > 0)
        {
            AgentTelemetry.UngroundedAnswers.Add(1);
            await sink.EmitAsync(new TextEvent(UngroundedFiguresNote), ct);
        }

        await EmitUsageAsync(sink, inputTokens, outputTokens, ct);

        // Saved only now, and only whole: a cancelled or failed turn returned above and left the stored session as it was.
        var history = agents.HistoryFor(role);
        history.SetMessages(session, HistoryTrimmer.Trim(history.GetMessages(session), options.MaxHistoryTokens));
        lease.Commit(await agent.SerializeSessionAsync(session, null, ct));
        return TurnOutcome.Completed;
    }

    private static void RecordTokens(string name, long inputTokens, long outputTokens)
    {
        AgentTelemetry.Tokens.Add(inputTokens,
            new KeyValuePair<string, object?>("model", name), new KeyValuePair<string, object?>("direction", "input"));
        AgentTelemetry.Tokens.Add(outputTokens,
            new KeyValuePair<string, object?>("model", name), new KeyValuePair<string, object?>("direction", "output"));
    }

    private static ValueTask EmitUsageAsync(IEventSink sink, long inputTokens, long outputTokens, CancellationToken ct) =>
        sink.EmitAsync(new UsageEvent(inputTokens, outputTokens), ct);
}
