using System.Text.Json;
using SqlAgent.Core.Agent;
using SqlAgent.Core.Tests.Agent.Fakes;

namespace SqlAgent.Core.Tests.Agent;

public class ConversationStoreTests
{
    private static JsonElement Json(string value) => JsonDocument.Parse($"\"{value}\"").RootElement;

    private static Guid CreateAndRelease(ConversationStore store, string sub, Role role = Role.Finance)
    {
        using var lease = store.Create(sub, role);
        return lease.ConversationId;
    }

    [Fact]
    public void The_owner_gets_their_conversation_back()
    {
        using var store = new ConversationStore();
        var id = CreateAndRelease(store, "alice");

        var status = store.TryAcquire("alice", Role.Finance, id, out var lease);

        Assert.Equal(AcquireStatus.Acquired, status);
        Assert.Equal(id, lease!.ConversationId);
        lease.Dispose();
    }

    [Fact]
    public void Another_user_or_role_or_an_unknown_id_is_simply_not_found()
    {
        using var store = new ConversationStore();
        var id = CreateAndRelease(store, "alice", Role.Finance);

        Assert.Equal(AcquireStatus.NotFound, store.TryAcquire("bob", Role.Finance, id, out var other));
        Assert.Null(other);
        Assert.Equal(AcquireStatus.NotFound, store.TryAcquire("alice", Role.SalesRep, id, out _));
        Assert.Equal(AcquireStatus.NotFound, store.TryAcquire("alice", Role.Finance, Guid.NewGuid(), out _));
        // The owner is unaffected by the failed attempts.
        Assert.Equal(AcquireStatus.Acquired, store.TryAcquire("alice", Role.Finance, id, out var mine));
        mine!.Dispose();
    }

    [Fact]
    public void A_second_run_on_a_busy_conversation_is_refused_until_the_first_ends()
    {
        using var store = new ConversationStore();
        using var first = store.Create("alice", Role.Finance);

        Assert.Equal(AcquireStatus.Busy, store.TryAcquire("alice", Role.Finance, first.ConversationId, out var second));
        Assert.Null(second);

        first.Dispose();
        Assert.Equal(AcquireStatus.Acquired, store.TryAcquire("alice", Role.Finance, first.ConversationId, out var third));
        third!.Dispose();
    }

    [Fact]
    public void Disposing_a_lease_twice_does_not_free_a_lock_that_someone_else_now_holds()
    {
        using var store = new ConversationStore();
        var first = store.Create("alice", Role.Finance);
        var id = first.ConversationId;
        first.Dispose();
        store.TryAcquire("alice", Role.Finance, id, out var second);

        first.Dispose();

        Assert.Equal(AcquireStatus.Busy, store.TryAcquire("alice", Role.Finance, id, out _));
        second!.Dispose();
    }

    [Fact]
    public void A_committed_session_is_there_next_time_and_an_uncommitted_one_is_not()
    {
        using var store = new ConversationStore();
        Guid id;
        using (var first = store.Create("alice", Role.Finance))
        {
            id = first.ConversationId;
            Assert.Null(first.Snapshot);
            first.Commit(Json("after turn 1"));
        }

        using (store.TryAcquire("alice", Role.Finance, id, out var second) == AcquireStatus.Acquired ? second : null)
        {
            Assert.Equal("after turn 1", second!.Snapshot!.Value.GetString());
            // A cancelled or failed turn ends here, without Commit.
        }

        store.TryAcquire("alice", Role.Finance, id, out var third);
        Assert.Equal("after turn 1", third!.Snapshot!.Value.GetString());
        third.Dispose();
    }

    [Fact]
    public void A_user_keeps_at_most_five_conversations_and_the_oldest_makes_room()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var store = new ConversationStore(new ConversationStoreOptions { MaxPerUser = 5 }, time);
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            ids.Add(CreateAndRelease(store, "alice"));
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(AcquireStatus.NotFound, store.TryAcquire("alice", Role.Finance, ids[0], out _));
        foreach (var id in ids.Skip(1))
        {
            Assert.Equal(AcquireStatus.Acquired, store.TryAcquire("alice", Role.Finance, id, out var lease));
            lease!.Dispose();
        }

        Assert.Equal(5, store.Count);
    }

    [Fact]
    public void One_users_conversations_never_push_out_anothers_below_the_total_limit()
    {
        using var store = new ConversationStore(new ConversationStoreOptions { MaxPerUser = 2 });
        var bobs = CreateAndRelease(store, "bob");
        for (var i = 0; i < 10; i++) CreateAndRelease(store, "alice");

        Assert.Equal(AcquireStatus.Acquired, store.TryAcquire("bob", Role.Finance, bobs, out var lease));
        lease!.Dispose();
    }

    [Fact]
    public void The_total_is_bounded_and_the_new_conversation_is_never_the_one_dropped()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var store = new ConversationStore(new ConversationStoreOptions { MaxTotal = 3, MaxPerUser = 5 }, time);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(CreateAndRelease(store, "user" + i));
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(3, store.Count);
        Assert.Equal(AcquireStatus.NotFound, store.TryAcquire("user0", Role.Finance, ids[0], out _));
        Assert.Equal(AcquireStatus.NotFound, store.TryAcquire("user1", Role.Finance, ids[1], out _));
        Assert.Equal(AcquireStatus.Acquired, store.TryAcquire("user4", Role.Finance, ids[4], out var newest));
        newest!.Dispose();
    }

    [Fact]
    public void A_conversation_expires_after_thirty_idle_minutes_and_use_keeps_it_alive()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var store = new ConversationStore(time: time);
        var idle = CreateAndRelease(store, "alice");
        var busy = CreateAndRelease(store, "alice");

        for (var minute = 0; minute < 3; minute++)
        {
            time.Advance(TimeSpan.FromMinutes(20));
            store.TryAcquire("alice", Role.Finance, busy, out var lease);
            lease!.Dispose();
        }

        Assert.Equal(AcquireStatus.NotFound, store.TryAcquire("alice", Role.Finance, idle, out _));
        Assert.Equal(AcquireStatus.Acquired, store.TryAcquire("alice", Role.Finance, busy, out var alive));
        alive!.Dispose();
    }
}
