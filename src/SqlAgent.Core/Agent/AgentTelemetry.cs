using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SqlAgent.Core.Agent;

/// <summary>
/// The agent's own spans and metrics. Add <see cref="SourceName"/> to the tracer provider and <see cref="MeterName"/>
/// to the meter provider to export them. Nothing recorded here carries row data or prompt text: spans hold counts,
/// timings, the role and violation codes only. There is deliberately no cost metric; cost is computed once in the eval
/// report from a dated list price.
/// </summary>
public static class AgentTelemetry
{
    public const string SourceName = "SqlAgent";

    public const string MeterName = "SqlAgent";

    /// <summary>Spans <c>guardrail.validate</c> and <c>sql.execute</c>, children of the turn's request (or eval item) span.</summary>
    public static readonly ActivitySource Source = new(SourceName);

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Turns whose answer was replaced by the deterministic "could not answer reliably" message.</summary>
    public static readonly Counter<long> FallbackAnswers =
        Meter.CreateCounter<long>("sqlagent.answers.fallback", description: "Answers replaced by the deterministic fallback message.");

    /// <summary>Answers that quoted a figure which appears in none of the turn's query results.</summary>
    public static readonly Counter<long> UngroundedAnswers =
        Meter.CreateCounter<long>("sqlagent.answers.ungrounded_figures", description: "Answers with a figure not found in the query results.");

    /// <summary>Tag <c>code</c>: the <see cref="Guardrails.ViolationCode"/>. A refusal with several violations counts once per code.</summary>
    public static readonly Counter<long> GuardrailBlocks =
        Meter.CreateCounter<long>("sqlagent.guardrail.blocks", description: "run_sql calls refused by the guardrail, by violation code.");

    /// <summary>Tags <c>model</c> and <c>direction</c> (<c>input</c> or <c>output</c>).</summary>
    public static readonly Counter<long> Tokens =
        Meter.CreateCounter<long>("sqlagent.tokens", unit: "{token}", description: "Tokens used by the language model.");

    /// <summary>Tags <c>role</c> and <c>outcome</c> (<c>ok</c> or <c>failed</c>).</summary>
    public static readonly Histogram<double> QueryDuration =
        Meter.CreateHistogram<double>("sqlagent.query.duration", unit: "ms", description: "Time to run one approved query, including a failed one.");

    /// <summary>Tags <c>role</c> and <c>outcome</c> (a <see cref="TurnOutcome"/>).</summary>
    public static readonly Histogram<double> TurnDuration =
        Meter.CreateHistogram<double>("sqlagent.turn.duration", unit: "ms", description: "Time to answer one question, end to end.");

    /// <summary>Turns whose tool loop was stopped by the model-call cap before the model could answer.</summary>
    public static readonly Counter<long> IterationCapHits =
        Meter.CreateCounter<long>("sqlagent.iteration_cap_hits", description: "Turns stopped by the iteration cap.");
}
