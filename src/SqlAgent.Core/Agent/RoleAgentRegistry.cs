using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SqlAgent.Core.Schema;

namespace SqlAgent.Core.Agent;

/// <summary>
/// One long-lived agent per role. An agent holds only what is the same for every user of the role: the instructions
/// and the role's few-shot examples. Nothing about a user lives here: tools are passed per run, bound to the caller's
/// server-side context, and the conversation is a separate session. So an agent can serve any number of users and
/// conversations at once without one seeing another's state.
/// </summary>
public sealed class RoleAgentRegistry
{
    private readonly IReadOnlyDictionary<Role, ChatClientAgent> _agents;

    public RoleAgentRegistry(
        IChatClient chatClient,
        ISchemaCatalog catalog,
        string systemPrompt,
        LlmOptions llm,
        ILoggerFactory? loggerFactory = null)
    {
        _agents = RoleExtensions.All.ToDictionary(role => role, role => new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = "sql-agent-" + role.ToKey().Replace('_', '-'),
                ChatOptions = new ChatOptions
                {
                    Instructions = BuildInstructions(systemPrompt, role, catalog.GetExamples(role)),
                    Temperature = llm.Temperature,
                },
                ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions()),
                // The client already carries function invocation and telemetry (ChatClientFactory): wrapping it again
                // would trace every call twice.
                UseProvidedChatClientAsIs = true,
            },
            loggerFactory));
    }

    public ChatClientAgent For(Role role) => _agents[role];

    /// <summary>The history provider of a role's agent, whose messages the store trims after each turn.</summary>
    public InMemoryChatHistoryProvider HistoryFor(Role role) =>
        (InMemoryChatHistoryProvider)_agents[role].ChatHistoryProvider!;

    /// <summary>The system prompt plus the role's examples. Nothing here depends on who is asking.</summary>
    public static string BuildInstructions(string systemPrompt, Role role, IReadOnlyList<SqlExample> examples)
    {
        var text = new StringBuilder(systemPrompt.TrimEnd());
        if (examples.Count > 0)
        {
            text.Append("\n\nExamples of questions with a correct query (the tables and columns are real; use them as a guide):");
            foreach (var example in examples)
            {
                text.Append("\nQuestion: ").Append(example.Question).Append("\nSQL: ").Append(example.Sql.Trim()).Append('\n');
            }
        }

        return text.ToString();
    }
}
