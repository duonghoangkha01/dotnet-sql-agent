namespace SqlAgent.IntegrationTests;

/// <summary>
/// A test that needs the local AdventureWorks stack, connecting as sqlagent_app. It is skipped unless
/// SQLAGENT_TEST_APP_CONNECTION is set, for example (SQL Server published with compose.dev.yml):
/// <c>Server=127.0.0.1,1433;Database=AdventureWorks2022;User Id=sqlagent_app;Password=...;TrustServerCertificate=True</c>.
/// A shared fixture that starts the database itself replaces this later.
/// </summary>
public sealed class LocalDbFactAttribute : FactAttribute
{
    public const string ConnectionStringVariable = "SQLAGENT_TEST_APP_CONNECTION";

    public LocalDbFactAttribute()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Skip = $"Set {ConnectionStringVariable} to run against the local database.";
        }
    }

    public static string? ConnectionString => Environment.GetEnvironmentVariable(ConnectionStringVariable);
}
