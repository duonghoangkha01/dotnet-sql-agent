using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SqlAgent.IntegrationTests.Fixtures;

/// <summary>
/// Runs a deploy/sql script the way <c>sqlcmd -I -b</c> does: <c>$(NAME)</c> variables are substituted, the text is
/// split into batches at lines holding only <c>GO</c>, and the first error aborts the run. One connection per script,
/// as each sqlcmd call starts a fresh session.
/// </summary>
internal static partial class SqlScriptRunner
{
    public static async Task RunAsync(
        string connectionString,
        string scriptPath,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken ct = default)
    {
        var text = File.ReadAllText(scriptPath);
        foreach (var (name, value) in variables) text = text.Replace($"$({name})", value, StringComparison.Ordinal);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        foreach (var batch in SplitBatches(text))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = 600;
            try
            {
                await command.ExecuteNonQueryAsync(ct);
            }
            catch (SqlException ex)
            {
                throw new InvalidOperationException($"{Path.GetFileName(scriptPath)} failed: {ex.Message}\n--- batch ---\n{batch}", ex);
            }
        }
    }

    public static IEnumerable<string> SplitBatches(string script) =>
        GoLine().Split(script).Select(b => b.Trim()).Where(b => b.Length > 0);

    [GeneratedRegex(@"^[ \t]*GO[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();
}
