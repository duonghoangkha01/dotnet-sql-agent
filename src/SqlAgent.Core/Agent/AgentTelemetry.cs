using System.Diagnostics.Metrics;

namespace SqlAgent.Core.Agent;

/// <summary>The agent's own metrics. Add <see cref="MeterName"/> to the meter provider to export them.</summary>
public static class AgentTelemetry
{
    public const string MeterName = "SqlAgent";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Turns whose answer was replaced by the deterministic "could not answer reliably" message.</summary>
    public static readonly Counter<long> FallbackAnswers =
        Meter.CreateCounter<long>("sqlagent.answers.fallback", description: "Answers replaced by the deterministic fallback message.");

    /// <summary>Answers that quoted a figure which appears in none of the turn's query results.</summary>
    public static readonly Counter<long> UngroundedAnswers =
        Meter.CreateCounter<long>("sqlagent.answers.ungrounded_figures", description: "Answers with a figure not found in the query results.");

    public static readonly Counter<long> GuardrailBlocks =
        Meter.CreateCounter<long>("sqlagent.sql.blocked", description: "run_sql calls refused by the guardrail.");
}
