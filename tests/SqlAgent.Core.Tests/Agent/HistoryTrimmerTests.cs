using Microsoft.Extensions.AI;
using SqlAgent.Core.Agent;

namespace SqlAgent.Core.Tests.Agent;

public class HistoryTrimmerTests
{
    private static ChatMessage User(string text) => new(ChatRole.User, text);

    private static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);

    private static string Words(int tokens) => new('x', tokens * 4);

    [Fact]
    public void History_inside_the_budget_is_unchanged()
    {
        List<ChatMessage> messages = [User("a"), Assistant("b"), User("c"), Assistant("d")];

        Assert.Equal(messages, HistoryTrimmer.Trim(messages, 6000));
    }

    [Fact]
    public void The_oldest_whole_turns_go_first()
    {
        List<ChatMessage> messages =
        [
            User("old " + Words(100)), Assistant(Words(100)),
            User("middle " + Words(100)), Assistant(Words(100)),
            User("new"), Assistant("answer"),
        ];

        var trimmed = HistoryTrimmer.Trim(messages, 250);

        Assert.Equal(4, trimmed.Count);
        Assert.StartsWith("middle", trimmed[0].Text);
    }

    [Fact]
    public void A_tool_call_is_never_separated_from_its_result()
    {
        var call = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "run_sql", new Dictionary<string, object?> { ["sql"] = "SELECT 1" })]);
        var result = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", Words(300))]);
        List<ChatMessage> messages = [User("first"), call, result, Assistant("done"), User("second"), Assistant("ok")];

        var trimmed = HistoryTrimmer.Trim(messages, 100);

        // Either the whole first turn is kept or none of it: no message in the middle of it starts the history.
        Assert.Equal(ChatRole.User, trimmed[0].Role);
        Assert.Equal("second", trimmed[0].Text);
        Assert.Equal(2, trimmed.Count);
    }

    [Fact]
    public void The_newest_turn_is_kept_even_when_it_alone_is_over_budget()
    {
        List<ChatMessage> messages = [User("old"), Assistant("old answer"), User(Words(5000)), Assistant(Words(5000))];

        var trimmed = HistoryTrimmer.Trim(messages, 100);

        Assert.Equal(2, trimmed.Count);
        Assert.Equal(ChatRole.User, trimmed[0].Role);
    }

    [Fact]
    public void The_result_is_within_budget_when_there_is_room()
    {
        var messages = new List<ChatMessage>();
        for (var turn = 0; turn < 20; turn++)
        {
            messages.Add(User(Words(200)));
            messages.Add(Assistant(Words(200)));
        }

        var trimmed = HistoryTrimmer.Trim(messages, 6000);

        Assert.True(HistoryTrimmer.EstimateTokens(trimmed) <= 6000);
        Assert.True(trimmed.Count >= 2);
        Assert.Equal(ChatRole.User, trimmed[0].Role);
    }
}
