namespace SqlAgent.Core.Agent;

/// <summary>Per-turn bounds and what the model may see. Defaults are the values the design settled on.</summary>
public sealed class AgentOptions
{
    public ResultVisibility ResultVisibility { get; init; } = ResultVisibility.Summary;

    /// <summary>Rows the model sees in <see cref="ResultVisibility.Summary"/> mode.</summary>
    public int SummaryRows { get; init; } = 5;

    /// <summary>Rows the model sees in <see cref="ResultVisibility.Rows"/> mode.</summary>
    public int MaxRowsForModel { get; init; } = 50;

    /// <summary>Total database time one turn may spend, over all its queries.</summary>
    public TimeSpan MaxSqlTimePerTurn { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary><c>run_sql</c> errors or refusals in a row after which the model is told to ask the user to rephrase.</summary>
    public int MaxConsecutiveSqlFailures { get; init; } = 3;

    /// <summary>Stored history is trimmed to this many estimated tokens, whole turns at a time.</summary>
    public int MaxHistoryTokens { get; init; } = 6000;
}
