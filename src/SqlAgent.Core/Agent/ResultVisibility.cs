namespace SqlAgent.Core.Agent;

/// <summary>
/// How much of a query result the model itself sees. The UI always receives the full rows through the event
/// stream; this only limits what is sent to the LLM provider.
/// </summary>
public enum ResultVisibility
{
    /// <summary>Column names and the row count only. For regulated data that must not reach a provider.</summary>
    None,

    /// <summary>Columns, row count and the first few rows. The default.</summary>
    Summary,

    /// <summary>Up to 50 rows as compact JSON.</summary>
    Rows,
}
