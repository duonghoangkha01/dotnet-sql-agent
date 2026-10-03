using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace SqlAgent.Core.Agent;

public sealed class ConversationStoreOptions
{
    public int MaxPerUser { get; init; } = 5;

    public int MaxTotal { get; init; } = 200;

    public TimeSpan SlidingExpiration { get; init; } = TimeSpan.FromMinutes(30);
}

public enum AcquireStatus
{
    Acquired,

    /// <summary>Unknown id, expired, or owned by someone else. Deliberately one status: the caller cannot tell which.</summary>
    NotFound,

    /// <summary>A turn is already running on this conversation.</summary>
    Busy,
}

/// <summary>One stored conversation. Only the store creates them.</summary>
public sealed class Conversation
{
    internal Conversation(Guid id, string sub, Role role)
    {
        Id = id;
        Sub = sub;
        Role = role;
    }

    public Guid Id { get; }

    public string Sub { get; }

    public Role Role { get; }

    /// <summary>The agent session as of the last completed turn; null before the first one.</summary>
    public JsonElement? Snapshot { get; internal set; }

    internal SemaphoreSlim Gate { get; } = new(1, 1);

    internal long LastUsedTicks;
}

/// <summary>
/// The exclusive right to run one turn on a conversation. Dispose it when the turn ends. The conversation changes only
/// through <see cref="Commit"/>: a turn that is cancelled or fails simply never commits, so its messages are discarded.
/// </summary>
public sealed class ConversationLease : IDisposable
{
    private readonly Conversation _conversation;
    private int _released;

    internal ConversationLease(Conversation conversation) => _conversation = conversation;

    public Guid ConversationId => _conversation.Id;

    /// <summary>The session as of the last completed turn; null for a conversation that has not had one.</summary>
    public JsonElement? Snapshot => _conversation.Snapshot;

    /// <summary>Stores the session of a completed turn.</summary>
    public void Commit(JsonElement snapshot) => _conversation.Snapshot = snapshot.Clone();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) _conversation.Gate.Release();
    }
}

/// <summary>
/// Conversations in memory, keyed by <c>(sub, role, conversationId)</c>: a request can only reach a conversation
/// its own token created, so a leaked or guessed id gets "not found". At most one turn runs per conversation at a
/// time. Bounded: a few conversations per user, a few hundred in all, and each expires after a period of disuse.
/// The oldest conversation makes room for a new one instead of the new one being refused.
/// </summary>
public sealed class ConversationStore : IDisposable
{
    private readonly ConversationStoreOptions _options;
    private readonly TimeProvider _time;
    private readonly MemoryCache _cache;
    private readonly object _gate = new();
    private readonly Dictionary<string, Conversation> _byKey = new();

    public ConversationStore(ConversationStoreOptions? options = null, TimeProvider? time = null)
    {
        _options = options ?? new ConversationStoreOptions();
        _time = time ?? TimeProvider.System;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = _options.MaxTotal, Clock = new TimeProviderClock(_time) });
    }

    public int Count
    {
        get { lock (_gate) return _byKey.Count; }
    }

    /// <summary>A new, empty conversation for this caller, locked for the first turn.</summary>
    public ConversationLease Create(string sub, Role role)
    {
        var conversation = new Conversation(Guid.NewGuid(), sub, role);
        conversation.Gate.Wait();

        lock (_gate)
        {
            // Make room first, so the cache's size limit is never what decides (it would drop the new entry silently).
            EvictWhile(() => _byKey.Values.Count(c => c.Sub == sub) >= _options.MaxPerUser, c => c.Sub == sub);
            EvictWhile(() => _byKey.Count >= _options.MaxTotal, _ => true);

            var key = Key(sub, role, conversation.Id);
            _byKey[key] = conversation;
            Touch(conversation);
            _cache.Set(key, conversation, new MemoryCacheEntryOptions
            {
                Size = 1,
                SlidingExpiration = _options.SlidingExpiration,
            }.RegisterPostEvictionCallback(OnEvicted));
        }

        return new ConversationLease(conversation);
    }

    /// <summary>Locks an existing conversation of this caller for a turn.</summary>
    public AcquireStatus TryAcquire(string sub, Role role, Guid conversationId, out ConversationLease? lease)
    {
        lease = null;
        if (!_cache.TryGetValue(Key(sub, role, conversationId), out Conversation? conversation) || conversation is null)
            return AcquireStatus.NotFound;

        if (!conversation.Gate.Wait(0)) return AcquireStatus.Busy;

        lock (_gate) Touch(conversation);
        lease = new ConversationLease(conversation);
        return AcquireStatus.Acquired;
    }

    public void Dispose() => _cache.Dispose();

    private static string Key(string sub, Role role, Guid id) => $"{sub}|{role.ToKey()}|{id:N}";

    private void Touch(Conversation conversation) => conversation.LastUsedTicks = _time.GetTimestamp();

    /// <summary>Removes the least recently used conversations matching <paramref name="candidate"/> while <paramref name="full"/> holds.</summary>
    private void EvictWhile(Func<bool> full, Func<Conversation, bool> candidate)
    {
        while (full())
        {
            var oldest = _byKey.Values.Where(candidate).MinBy(c => c.LastUsedTicks);
            if (oldest is null) return;
            _cache.Remove(Key(oldest.Sub, oldest.Role, oldest.Id));
            _byKey.Remove(Key(oldest.Sub, oldest.Role, oldest.Id));
        }
    }

    private void OnEvicted(object key, object? value, EvictionReason reason, object? state)
    {
        // Replaced entries and the explicit removals above are handled by their callers.
        if (reason is EvictionReason.Replaced or EvictionReason.Removed) return;
        lock (_gate) _byKey.Remove((string)key);
    }

    private sealed class TimeProviderClock(TimeProvider time) : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }
}
