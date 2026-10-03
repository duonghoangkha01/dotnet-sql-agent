namespace SqlAgent.Core.Execution;

/// <param name="Type">The SQL Server type name, for example <c>nvarchar</c> or <c>money</c>.</param>
public sealed record QueryColumn(string Name, string Type);

/// <param name="Rows">One array per row, in column order; SQL NULL is <c>null</c>.</param>
/// <param name="Truncated">True when more rows exist than were returned. A UI shows "500+"; a total needs COUNT(*).</param>
public sealed record QueryResult(
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<object?[]> Rows,
    bool Truncated,
    long ElapsedMs)
{
    /// <summary>Rows returned, not rows that exist (see <see cref="Truncated"/>).</summary>
    public int RowCount => Rows.Count;
}

public enum QueryFailure
{
    /// <summary>The database rejected the query itself (syntax, unknown column, ...). The message is meant for the model.</summary>
    InvalidQuery,
    Timeout,
    /// <summary>Anything else (permissions, arithmetic errors, server trouble). The message is generic; details are logged.</summary>
    Failed,
}

/// <summary>A query that ran and failed. <see cref="Exception.Message"/> is safe to show the model.</summary>
public sealed class QueryExecutionException(QueryFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public QueryFailure Failure { get; } = failure;
}
