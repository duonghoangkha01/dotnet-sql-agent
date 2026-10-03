using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;
using SqlAgent.Core;
using SqlAgent.Core.Agent;

namespace SqlAgent.Api.Endpoints;

public sealed record ChatRequest(string? ConversationId, string? Message);

public sealed record SseOptions(TimeSpan Heartbeat)
{
    public static SseOptions Default { get; } = new(TimeSpan.FromSeconds(15));
}

public static class ChatEndpoints
{
    public const string ChatRateLimitPolicy = "chat";

    /// <summary>The audit log keeps 2,000 characters of the question; a longer message is refused rather than cut.</summary>
    public const int MaxMessageLength = 2000;

    public static void MapChatEndpoints(this IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/chat/stream", StreamChat)
            .RequireAuthorization()
            .RequireRateLimiting(ChatRateLimitPolicy);

    /// <summary>
    /// Every refusal that can be decided up front (bad input, 404, 409, 429) happens before the first byte of the stream,
    /// so it is a normal HTTP status. After that the response is a stream of events written by a single writer task.
    /// </summary>
    private static async Task<IResult> StreamChat(
        HttpContext http,
        ChatRequest request,
        ConversationStore store,
        RunSlots slots,
        AgentTurnRunner runner,
        SseOptions sse,
        ILogger<AgentTurnRunner> logger)
    {
        var identity = AuthEndpoints.ReadUser(http.User);
        if (identity is null) return Error(StatusCodes.Status403Forbidden, "The token does not describe a usable user.");
        var (sub, user) = identity.Value;

        var message = request.Message?.Trim();
        if (string.IsNullOrEmpty(message)) return Error(StatusCodes.Status400BadRequest, "message is required.");
        if (message.Length > MaxMessageLength) return Error(StatusCodes.Status400BadRequest, $"message is longer than {MaxMessageLength} characters.");

        switch (slots.TryEnter(sub, out var slot))
        {
            case SlotStatus.UserBusy:
                return Error(StatusCodes.Status409Conflict, "You already have a question running. Wait for it to finish.");
            case SlotStatus.ServerBusy:
                return Error(StatusCodes.Status429TooManyRequests, "The server is busy. Try again in a moment.");
        }

        ConversationLease? lease = null;
        try
        {
            if (request.ConversationId is null)
            {
                lease = store.Create(sub, user.Role);
            }
            else
            {
                // A malformed id, an unknown one and someone else's are all the same answer.
                var found = Guid.TryParse(request.ConversationId, out var id)
                    ? store.TryAcquire(sub, user.Role, id, out lease)
                    : AcquireStatus.NotFound;
                if (found == AcquireStatus.NotFound) return Error(StatusCodes.Status404NotFound, "Conversation not found.");
                if (found == AcquireStatus.Busy) return Error(StatusCodes.Status409Conflict, "This conversation already has a question running.");
            }
        }
        finally
        {
            if (lease is null) slot!.Dispose();
        }

        await RunStreamAsync(http, new TurnRequest(sub, user, message, TraceId()), lease!, slot!, runner, sse, logger);
        return Results.Empty;
    }

    private static async Task RunStreamAsync(
        HttpContext http,
        TurnRequest turn,
        ConversationLease lease,
        IDisposable slot,
        AgentTurnRunner runner,
        SseOptions sse,
        ILogger logger)
    {
        var ct = http.RequestAborted;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no"; // nginx must not hold events back
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var sink = new ChannelEventSink();
        var writer = WriteEventsAsync(sink.Reader, http.Response, sse.Heartbeat, ct);
        try
        {
            await sink.EmitAsync(new SessionEvent(lease.ConversationId.ToString()));
            await sink.EmitAsync(new TraceEvent(turn.TraceId!));
            await runner.RunAsync(turn, lease, sink, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client left: the turn is discarded and there is nobody to tell.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The chat turn failed in conversation {ConversationId}.", lease.ConversationId);
            if (!ct.IsCancellationRequested) await sink.EmitAsync(new ErrorEvent("Something went wrong. Please try again."));
        }
        finally
        {
            // The last event of every stream whose client is still there.
            try
            {
                if (!ct.IsCancellationRequested) await sink.EmitAsync(new DoneEvent());
                sink.Complete();
                await writer;
            }
            catch (Exception ex)
            {
                // A faulted writer must not keep the user's conversation and run slot locked for good.
                logger.LogWarning(ex, "The event stream of conversation {ConversationId} ended with an error.", lease.ConversationId);
            }
            finally
            {
                lease.Dispose();
                slot.Dispose();
            }
        }
    }

    /// <summary>The only code that writes to the response: events in order, a comment line when idle so proxies keep the connection open.</summary>
    private static async Task WriteEventsAsync(ChannelReader<SseEvent> events, HttpResponse response, TimeSpan heartbeat, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(heartbeat);
                bool more;
                try
                {
                    more = await events.WaitToReadAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await WriteAsync(response, ": ping\n\n", ct);
                    continue;
                }

                if (!more) return;
                while (events.TryRead(out var evt))
                {
                    await WriteAsync(response, $"event: {evt.EventName}\ndata: {evt.ToJson()}\n\n", ct);
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested || response.HttpContext.RequestAborted.IsCancellationRequested)
        {
            // The client disconnected mid-write.
        }
    }

    private static async Task WriteAsync(HttpResponse response, string text, CancellationToken ct)
    {
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        await response.Body.FlushAsync(ct);
    }

    private static string TraceId() => Activity.Current?.TraceId.ToString() ?? ActivityTraceId.CreateRandom().ToString();

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);
}
