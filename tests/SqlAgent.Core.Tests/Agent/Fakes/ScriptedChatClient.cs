using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace SqlAgent.Core.Tests.Agent.Fakes;

/// <summary>What the fake model does for one call: yields the streamed updates of its reply.</summary>
public delegate IAsyncEnumerable<ChatResponseUpdate> ScriptStep(IReadOnlyList<ChatMessage> messages, ChatOptions? options, CancellationToken ct);

/// <summary>
/// A model that follows a script, one step per call. Wrapped by <c>ChatClientFactory.Wrap</c> it goes through the real
/// function-invocation loop, so the real tools run: only the model's choices are scripted. Linked into the
/// integration tests too, so both projects drive the agent the same way.
/// </summary>
public sealed class ScriptedChatClient(params ScriptStep[] steps) : IChatClient
{
    private readonly Queue<ScriptStep> _steps = new(steps);
    private readonly object _gate = new();

    /// <summary>The messages and options of every call, in order (a copy taken when the call was made).</summary>
    public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

    public static ScriptStep Reply(string text, long inputTokens = 10, long outputTokens = 5) => (_, _, _) => Stream(
        new ChatResponseUpdate(ChatRole.Assistant, text),
        Usage(inputTokens, outputTokens));

    /// <summary>The model calls a tool; optional <paramref name="narration"/> is text it says first.</summary>
    public static ScriptStep Call(string tool, Dictionary<string, object?>? args = null, string? callId = null, string? narration = null) => (_, _, _) =>
    {
        var updates = new List<ChatResponseUpdate>();
        if (narration is not null) updates.Add(new ChatResponseUpdate(ChatRole.Assistant, narration));
        updates.Add(new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent(callId ?? Guid.NewGuid().ToString("N"), tool, args ?? [])]));
        updates.Add(Usage(10, 5));
        return Stream(updates.ToArray());
    };

    public static ScriptStep RunSql(string sql, string purpose = "test", string? callId = null) =>
        Call("run_sql", new() { ["sql"] = sql, ["purpose"] = purpose }, callId);

    public static ScriptStep Throw(Exception exception) => (_, _, _) => Failing(exception);

    /// <summary>Never answers; ends only when cancelled. For client-abort tests.</summary>
    public static ScriptStep Hang() => (_, _, ct) => Hanging(ct);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var snapshot = messages.ToList();
        ScriptStep step;
        lock (_gate)
        {
            Calls.Add((snapshot, options));
            step = _steps.Count > 0 ? _steps.Dequeue() : throw new InvalidOperationException("The scripted model was called more often than its script allows.");
        }

        return step(snapshot, options, cancellationToken);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)) updates.Add(update);
        return updates.ToChatResponse();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static ChatResponseUpdate Usage(long input, long output) =>
        new(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = input, OutputTokenCount = output })]);

    private static async IAsyncEnumerable<ChatResponseUpdate> Stream(params ChatResponseUpdate[] updates)
    {
        foreach (var update in updates)
        {
            await Task.Yield();
            yield return update;
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Failing(Exception exception)
    {
        await Task.Yield();
        throw exception;
#pragma warning disable CS0162 // unreachable: the iterator needs a yield to be an iterator
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Hanging([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }
}
