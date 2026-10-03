using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlAgent.Core.Guardrails;

namespace SqlAgent.Core.Governance;

/// <summary>One <c>run_sql</c> call, written once after it finished (or was refused).</summary>
/// <param name="ReturnedRows">Null when the query did not run to completion (refused, failed, cancelled).</param>
public sealed record AuditEntry(
    string Sub,
    Role Role,
    Guid ConversationId,
    string? Question,
    string Sql,
    bool Allowed,
    IReadOnlyList<Violation> Violations,
    int? ReturnedRows,
    long? ElapsedMs,
    string? TraceId,
    DateTimeOffset OccurredUtc);

public interface IAuditSink
{
    /// <summary>Must not throw: a failed audit write is logged, it never fails the user's turn.</summary>
    ValueTask WriteAsync(AuditEntry entry);
}

/// <summary>For evals and tests that do not keep an audit trail.</summary>
public sealed class NullAuditSink : IAuditSink
{
    public static readonly NullAuditSink Instance = new();

    public ValueTask WriteAsync(AuditEntry entry) => ValueTask.CompletedTask;
}

/// <summary>
/// Appends a row to <c>SqlAgent.dbo.AuditLog</c> as <c>sqlagent_app</c>, which may insert and select but not update
/// or delete (deploy/sql/30-sqlagent-app-db.sql). So every row is written once, with its final figures.
/// </summary>
public sealed class SqlAuditSink(string appConnectionString, ILogger<SqlAuditSink>? logger = null) : IAuditSink
{
    public const int MaxQuestionLength = 2000;

    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<SqlAuditSink> _logger = logger ?? NullLogger<SqlAuditSink>.Instance;

    public async ValueTask WriteAsync(AuditEntry entry)
    {
        // Not tied to the request: a client that disconnected mid-turn must not erase the record of a query that ran.
        using var timeout = new CancellationTokenSource(WriteTimeout);
        try
        {
            var builder = new SqlConnectionStringBuilder(appConnectionString) { InitialCatalog = "SqlAgent" };
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(timeout.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT dbo.AuditLog (OccurredUtc, Sub, [Role], ConversationId, Question, [Sql], Allowed, Violations, ReturnedRows, ElapsedMs, TraceId)
                VALUES (@occurred, @sub, @role, @conversation, @question, @sql, @allowed, @violations, @rows, @elapsed, @trace)
                """;
            command.Parameters.AddWithValue("@occurred", entry.OccurredUtc.UtcDateTime);
            command.Parameters.AddWithValue("@sub", entry.Sub);
            command.Parameters.AddWithValue("@role", entry.Role.ToKey());
            command.Parameters.AddWithValue("@conversation", entry.ConversationId);
            command.Parameters.AddWithValue("@question", (object?)Truncate(entry.Question) ?? DBNull.Value);
            command.Parameters.AddWithValue("@sql", entry.Sql);
            command.Parameters.AddWithValue("@allowed", entry.Allowed);
            command.Parameters.AddWithValue("@violations", entry.Violations.Count == 0
                ? DBNull.Value
                : JsonSerializer.Serialize(entry.Violations.Select(v => new { code = v.Code.ToString(), message = v.Message })));
            command.Parameters.AddWithValue("@rows", (object?)entry.ReturnedRows ?? DBNull.Value);
            command.Parameters.AddWithValue("@elapsed", entry.ElapsedMs is { } ms ? (object)(int)Math.Min(ms, int.MaxValue) : DBNull.Value);
            command.Parameters.AddWithValue("@trace", (object?)entry.TraceId ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            // The query already ran (or was refused); failing the turn now would not undo that.
            _logger.LogError(ex, "Could not write an audit row for {Sub} in conversation {ConversationId}.", entry.Sub, entry.ConversationId);
        }
    }

    private static string? Truncate(string? text) =>
        text is { Length: > MaxQuestionLength } ? text[..MaxQuestionLength] : text;
}
