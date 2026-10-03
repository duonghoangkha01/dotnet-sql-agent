using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlAgent.Core.Agent;

namespace SqlAgent.Core.Execution;

/// <summary>The seam the agent's tools run queries through, so tests and evals can substitute it.</summary>
public interface IQueryExecutor
{
    /// <exception cref="QueryExecutionException">The query ran and failed; the message is safe to show the model.</exception>
    Task<QueryResult> ExecuteAsync(string sql, UserContext user, CancellationToken ct = default);
}

/// <summary>
/// Runs a query the guardrail approved, as the caller's role user, with the territory fixed in a read-only
/// session context (which row-level security reads), a time limit and a row cap. It does not validate the SQL:
/// the caller passes <see cref="Guardrails.GuardrailResult.Sql"/>.
/// </summary>
public sealed class SafeQueryExecutor(
    RoleConnectionFactory connections,
    QueryLimits? limits = null,
    ILogger<SafeQueryExecutor>? logger = null) : IQueryExecutor
{
    // Database errors that describe the query, not the data or the server: they are what the model needs to fix
    // its SQL. Everything else (permissions, conversions that would echo a hidden value, ...) stays generic, so
    // it cannot be used to probe rows or objects the caller may not see.
    private static readonly HashSet<int> ErrorsForTheModel =
    [
        102,  // incorrect syntax
        105,  // unclosed quotation mark
        156,  // incorrect syntax near keyword
        174,  // function needs more arguments
        195,  // not a recognized built-in function
        207,  // invalid column name
        208,  // invalid object name
        209,  // ambiguous column name
        319,  // incorrect syntax near WITH: previous statement must be terminated
        1033, // ORDER BY invalid in derived tables and subqueries
        4104, // multi-part identifier could not be bound
        4145, // expression of non-boolean type where a condition is expected
        8120, // column invalid in select list: not in an aggregate or GROUP BY
        8121, // column invalid in HAVING
        8155, // no column name specified
        8156, // column specified multiple times
    ];

    private readonly QueryLimits _limits = limits ?? new QueryLimits();
    private readonly ILogger<SafeQueryExecutor> _logger = logger ?? NullLogger<SafeQueryExecutor>.Instance;

    /// <exception cref="QueryExecutionException">The query ran and failed; the message is safe to show the model.</exception>
    public async Task<QueryResult> ExecuteAsync(string sql, UserContext user, CancellationToken ct = default)
    {
        // Counts, timings and the role only: no SQL text and no row values go into the span or the metric.
        using var span = AgentTelemetry.Source.StartActivity("sql.execute");
        span?.SetTag("role", user.Role.ToKey());
        var clock = Stopwatch.StartNew();
        var outcome = "failed";
        try
        {
            var result = await RunAsync(sql, user, ct);
            outcome = "ok";
            span?.SetTag("rowCount", result.RowCount);
            span?.SetTag("truncated", result.Truncated);
            span?.SetTag("elapsedMs", result.ElapsedMs);
            return result;
        }
        finally
        {
            span?.SetTag("outcome", outcome);
            AgentTelemetry.QueryDuration.Record(clock.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("role", user.Role.ToKey()),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    private async Task<QueryResult> RunAsync(string sql, UserContext user, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(user.Role, ct);
        await PrepareSessionAsync(connection, user, ct);

        var stopwatch = Stopwatch.StartNew();
        var columns = new List<QueryColumn>();
        var rows = new List<object?[]>();
        var capped = false;
        var resultBytes = 0L;
        QueryExecutionException? unreadable = null;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = (int)Math.Ceiling(_limits.CommandTimeout.TotalSeconds);

        try
        {
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    columns.Add(new QueryColumn(reader.GetName(i), reader.GetDataTypeName(i)));
                    unreadable ??= CheckReadable(reader, i);
                }

                while (unreadable is null && await reader.ReadAsync(ct))
                {
                    // One row past the cap proves there is more, without reading it all.
                    if (rows.Count == _limits.MaxRows)
                    {
                        capped = true;
                        break;
                    }

                    var values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    var converted = Array.ConvertAll(values, v => v is DBNull ? null : v);

                    // A row count alone does not bound a result: one STRING_AGG or REPLICATE cell can itself be
                    // huge. Stop at the byte budget the same way as the row cap, even on the very first row.
                    resultBytes += EstimateSize(converted);
                    if (resultBytes > _limits.MaxResultBytes)
                    {
                        capped = true;
                        break;
                    }

                    rows.Add(converted);
                }

                // Stop the server producing the rest. Closing the reader then reports that cancellation as an error.
                if (capped || unreadable is not null) command.Cancel();
            }
        }
        // Keyed on flags, not on the error text: only the cancel requested just above is expected.
        catch (SqlException) when (capped || unreadable is not null)
        {
        }
        catch (SqlException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (SqlException ex)
        {
            throw Map(ex);
        }

        if (unreadable is not null) throw unreadable;

        return new QueryResult(columns, rows, capped, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>Rough size of one row's values, for <see cref="QueryLimits.MaxResultBytes"/>. Only needs to be in the right order of magnitude.</summary>
    private static long EstimateSize(object?[] values)
    {
        long total = 0;
        foreach (var value in values)
        {
            total += value switch
            {
                null => 1,
                string s => (long)s.Length * sizeof(char),
                byte[] b => b.LongLength,
                _ => 16, // numbers, dates, bools, guids: all small and fixed-size
            };
        }

        return total;
    }

    /// <summary>
    /// CLR types (geography, geometry, hierarchyid) cannot be materialized without the SQL Server types assembly:
    /// reading one would throw a FileNotFoundException mid-result. Say so up front, with a way out for the model.
    /// </summary>
    private static QueryExecutionException? CheckReadable(SqlDataReader reader, int ordinal)
    {
        if (reader.GetFieldType(ordinal) is not null) return null;

        var typeName = reader.GetDataTypeName(ordinal).Split('.')[^1];
        var advice = typeName.Equals("hierarchyid", StringComparison.OrdinalIgnoreCase)
            ? "Leave it out, or convert it to text with CAST(column AS nvarchar(4000))."
            : "Leave it out of the query.";
        return new QueryExecutionException(QueryFailure.InvalidQuery,
            $"Column '{reader.GetName(ordinal)}' has the type {typeName}, which cannot be returned. {advice}");
    }

    /// <summary>QUOTED_IDENTIFIER ON (as the guardrail parsed the query), and the territory, read-only, for a sales rep.</summary>
    private async Task PrepareSessionAsync(SqlConnection connection, UserContext user, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        if (user.Role == Role.SalesRep)
        {
            // read_only = 1: user SQL cannot change it afterwards. A pooled connection starts clean on reuse.
            command.CommandText = "SET QUOTED_IDENTIFIER ON; " +
                                  "EXEC sys.sp_set_session_context @key = N'territory_id', @value = @territory, @read_only = 1;";
            command.Parameters.Add(new SqlParameter("@territory", SqlDbType.Int) { Value = user.TerritoryId });
        }
        else
        {
            command.CommandText = "SET QUOTED_IDENTIFIER ON;";
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private QueryExecutionException Map(SqlException ex)
    {
        if (ex.Number == -2)
        {
            return new QueryExecutionException(QueryFailure.Timeout,
                $"The query was stopped after {_limits.CommandTimeout.TotalSeconds:0} seconds. " +
                "Make it cheaper: filter earlier, aggregate in SQL, and avoid cross joins.", ex);
        }

        if (ErrorsForTheModel.Contains(ex.Number))
            return new QueryExecutionException(QueryFailure.InvalidQuery, ex.Errors[0].Message, ex);

        _logger.LogWarning(ex, "Query failed with SQL error {Number} (state {State}, class {Class}).", ex.Number, ex.State, ex.Class);
        return new QueryExecutionException(QueryFailure.Failed, "The query could not be run. Try a simpler query.", ex);
    }
}
