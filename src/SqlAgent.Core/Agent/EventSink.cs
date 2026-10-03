using System.Threading.Channels;

namespace SqlAgent.Core.Agent;

/// <summary>Where the tools and the turn runner publish events. Implementations must be safe to call from any thread.</summary>
public interface IEventSink
{
    ValueTask EmitAsync(SseEvent evt, CancellationToken ct = default);
}

/// <summary>
/// An <see cref="IEventSink"/> over a channel with a single reader: the API's one writer task serializes the channel
/// to the response, so tool events and the agent loop never write to the response concurrently.
/// </summary>
public sealed class ChannelEventSink : IEventSink
{
    private readonly Channel<SseEvent> _channel = Channel.CreateUnbounded<SseEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ChannelReader<SseEvent> Reader => _channel.Reader;

    public ValueTask EmitAsync(SseEvent evt, CancellationToken ct = default)
    {
        // Unbounded: TryWrite only fails after Complete, when nobody is listening any more.
        _channel.Writer.TryWrite(evt);
        return ValueTask.CompletedTask;
    }

    /// <summary>No more events: the writer task drains what is queued and ends.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
