using System.Collections.Concurrent;

namespace SqlAgent.Api;

public enum SlotStatus
{
    Granted,

    /// <summary>This user already has a run in progress.</summary>
    UserBusy,

    /// <summary>The server is running as many chats as it allows.</summary>
    ServerBusy,
}

/// <summary>
/// Limits how many chat runs are active: one per user and a fixed number overall. Checked before the response starts,
/// so a refusal is an ordinary HTTP status. Held for the whole run, so a user cannot start a second one while the
/// first is still talking to the model or the database.
/// </summary>
public sealed class RunSlots(int maxConcurrent)
{
    private readonly SemaphoreSlim _global = new(maxConcurrent, maxConcurrent);
    private readonly ConcurrentDictionary<string, byte> _active = new();

    public SlotStatus TryEnter(string sub, out IDisposable? slot)
    {
        slot = null;
        if (!_active.TryAdd(sub, 0)) return SlotStatus.UserBusy;

        if (!_global.Wait(0))
        {
            _active.TryRemove(sub, out _);
            return SlotStatus.ServerBusy;
        }

        slot = new Slot(this, sub);
        return SlotStatus.Granted;
    }

    private sealed class Slot(RunSlots owner, string sub) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            owner._active.TryRemove(sub, out _);
            owner._global.Release();
        }
    }
}
