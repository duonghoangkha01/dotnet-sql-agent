using System.Text.Json;
using Microsoft.Extensions.AI;
using SqlAgent.Core.Schema;

namespace SqlAgent.Core.Agent;

/// <summary>
/// Keeps a conversation's stored history inside a token budget by dropping the oldest turns. A turn is a user
/// message and everything the model did in answer, so a tool call is never separated from its result (providers
/// reject a result with no call). Tokens are estimated, not counted: see <see cref="PromptBudget.EstimateTokens"/>.
/// </summary>
public static class HistoryTrimmer
{
    private const int PerMessageOverhead = 4;
    private const int UnknownContentTokens = 16;

    public static int EstimateTokens(IEnumerable<ChatMessage> messages) => messages.Sum(EstimateTokens);

    public static int EstimateTokens(ChatMessage message)
    {
        var tokens = PerMessageOverhead;
        foreach (var content in message.Contents)
        {
            tokens += content switch
            {
                TextContent text => PromptBudget.EstimateTokens(text.Text ?? ""),
                TextReasoningContent reasoning => PromptBudget.EstimateTokens(reasoning.Text ?? ""),
                FunctionCallContent call => PromptBudget.EstimateTokens(call.Name + JsonSerializer.Serialize(call.Arguments)),
                FunctionResultContent result => PromptBudget.EstimateTokens(result.Result?.ToString() ?? ""),
                _ => UnknownContentTokens,
            };
        }

        return tokens;
    }

    /// <summary>
    /// The most recent whole turns that fit in <paramref name="maxTokens"/>. The newest turn is always kept, even if it
    /// alone is over budget: dropping it would lose the very question being answered.
    /// </summary>
    public static List<ChatMessage> Trim(IReadOnlyList<ChatMessage> messages, int maxTokens)
    {
        var starts = new List<int>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == ChatRole.User) starts.Add(i);
        }

        // Anything before the first user message is not part of a turn; leave such history alone.
        if (starts.Count <= 1) return messages.ToList();

        var total = EstimateTokens(messages);
        var firstKept = 0;
        while (total > maxTokens && firstKept < starts.Count - 1)
        {
            var from = starts[firstKept];
            var to = starts[firstKept + 1];
            for (var i = from; i < to; i++) total -= EstimateTokens(messages[i]);
            firstKept++;
        }

        return messages.Skip(starts[firstKept]).ToList();
    }
}
