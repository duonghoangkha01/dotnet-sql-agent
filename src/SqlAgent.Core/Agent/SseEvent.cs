using System.Text.Json;
using System.Text.Json.Serialization;
using SqlAgent.Core.Execution;

namespace SqlAgent.Core.Agent;

/// <summary>
/// One event of the chat stream. The record types are the contract in docs/sse-contract.md, which the web client's
/// types mirror: the event name is the SSE <c>event:</c> field and the record's properties (camelCase) the JSON
/// <c>data:</c>. Null properties are omitted.
/// </summary>
public abstract record SseEvent
{
    /// <summary>The SSE event name.</summary>
    public abstract string EventName { get; }

    /// <summary>One shared set of options so every event serializes the same way.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The JSON data of this event (serialized by its runtime type, so derived properties are included).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, GetType(), JsonOptions);
}

/// <summary>Always first. Carries the server-generated conversation id.</summary>
public sealed record SessionEvent(string ConversationId) : SseEvent
{
    [JsonIgnore] public override string EventName => "session";
}

/// <summary>Emitted early, right after <see cref="SessionEvent"/>, so a trace can be opened while the turn runs.</summary>
public sealed record TraceEvent(string TraceId) : SseEvent
{
    [JsonIgnore] public override string EventName => "trace";
}

public sealed record TextEvent(string Delta) : SseEvent
{
    [JsonIgnore] public override string EventName => "text";
}

/// <summary>The model called a tool. <c>Sql</c> and <c>Purpose</c> are set for <c>run_sql</c> only.</summary>
public sealed record ToolCallEvent(string CallId, string Tool, string? Sql = null, string? Purpose = null) : SseEvent
{
    [JsonIgnore] public override string EventName => "tool_call";
}

/// <summary>May repeat within a turn. Rows go to the UI from here, whatever the model is allowed to see.</summary>
public sealed record QueryResultEvent(
    string CallId,
    string Sql,
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<object?[]> Rows,
    int RowCount,
    bool Truncated,
    long ElapsedMs) : SseEvent
{
    [JsonIgnore] public override string EventName => "query_result";
}

public sealed record QueryErrorEvent(string CallId, string Sql, string Message) : SseEvent
{
    [JsonIgnore] public override string EventName => "query_error";
}

public sealed record ViolationInfo(string Code, string Message);

public sealed record GuardrailBlockedEvent(string CallId, string Sql, IReadOnlyList<ViolationInfo> Violations) : SseEvent
{
    [JsonIgnore] public override string EventName => "guardrail_blocked";
}

/// <summary>Tokens only; there is no cost estimate.</summary>
public sealed record UsageEvent(long InputTokens, long OutputTokens) : SseEvent
{
    [JsonIgnore] public override string EventName => "usage";
}

public sealed record ErrorEvent(string Message) : SseEvent
{
    [JsonIgnore] public override string EventName => "error";
}

/// <summary>Always last, sent from a <c>finally</c> block while the client is still connected.</summary>
public sealed record DoneEvent : SseEvent
{
    [JsonIgnore] public override string EventName => "done";
}
