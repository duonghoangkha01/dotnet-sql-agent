using SqlAgent.Core;
using SqlAgent.Core.Agent;
using SqlAgent.Core.Execution;

namespace SqlAgent.Api;

/// <summary>
/// The API's settings, read from configuration (environment variables in the container, as in <c>.env</c>).
/// Only the signing key is checked at startup: the database settings are read when the services that need them are
/// first built, so the API's HTTP layer can be hosted in tests without a database.
/// </summary>
public sealed class ApiSettings(IConfiguration configuration)
{
    public const int MinSigningKeyLength = 32;

    public const string Issuer = "sqlagent";
    public const string Audience = "sqlagent-api";

    /// <exception cref="InvalidOperationException">The key is missing or too short to be a secret.</exception>
    public string JwtSigningKey => Required("JWT_SIGNING_KEY") is { Length: >= MinSigningKeyLength } key
        ? key
        : throw new InvalidOperationException($"JWT_SIGNING_KEY must be at least {MinSigningKeyLength} characters. Run scripts/init-env.sh.");

    /// <summary>Whether <c>POST /api/auth/demo-token</c> exists. Off unless explicitly turned on.</summary>
    public bool DemoAuth => Flag("DEMO_AUTH", false);

    public TimeSpan TokenLifetime => TimeSpan.FromMinutes(Number("TOKEN_LIFETIME_MINUTES", 60));

    /// <summary>Chat requests per minute and user.</summary>
    public int ChatRequestsPerMinute => Number("CHAT_RATE_LIMIT_PER_MINUTE", 10);

    /// <summary>Token requests per minute and client address.</summary>
    public int DemoTokenRequestsPerMinute => Number("DEMO_TOKEN_RATE_LIMIT_PER_MINUTE", 10);

    /// <summary>Chat runs allowed at once across all users.</summary>
    public int MaxConcurrentRuns => Number("MAX_CONCURRENT_RUNS", 4);

    public ResultVisibility ResultVisibility => configuration["RESULT_VISIBILITY"] is { Length: > 0 } text
        ? Enum.TryParse<ResultVisibility>(text, ignoreCase: true, out var visibility)
            ? visibility
            : throw new InvalidOperationException("RESULT_VISIBILITY must be None, Summary or Rows.")
        : ResultVisibility.Summary;

    public LlmOptions Llm => LlmOptions.FromEnvironment(name => configuration[name]);

    public string SqlServer => configuration["SQL_SERVER"] is { Length: > 0 } server ? server : "sqlserver";

    public string SqlDatabase => configuration["SQL_DATABASE"] is { Length: > 0 } db ? db : "AdventureWorks2022";

    /// <summary>For the local container's self-signed certificate. Turn off against a server with a real one.</summary>
    public bool TrustServerCertificate => Flag("SQL_TRUST_SERVER_CERTIFICATE", true);

    public RoleConnectionFactory CreateRoleConnections() => new(
        SqlServer,
        SqlDatabase,
        new Dictionary<Role, string>
        {
            [Role.SalesRep] = Required("SQL_SALES_REP_PASSWORD"),
            [Role.Finance] = Required("SQL_FINANCE_PASSWORD"),
            [Role.Admin] = Required("SQL_ADMIN_PASSWORD"),
        },
        TrustServerCertificate);

    /// <summary>The connection string of <c>sqlagent_app</c>: schema metadata and the audit log, never business data.</summary>
    public string AppConnectionString => new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
    {
        DataSource = SqlServer,
        InitialCatalog = SqlDatabase,
        UserID = "sqlagent_app",
        Password = Required("SQL_APP_PASSWORD"),
        TrustServerCertificate = TrustServerCertificate,
        ApplicationName = "sqlagent-app",
        PersistSecurityInfo = false,
    }.ConnectionString;

    private string Required(string name) =>
        configuration[name] is { Length: > 0 } value ? value : throw new InvalidOperationException($"{name} is not set.");

    private bool Flag(string name, bool fallback) => configuration[name] is { Length: > 0 } value
        ? value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1"
        : fallback;

    private int Number(string name, int fallback) => configuration[name] is { Length: > 0 } value
        ? int.TryParse(value, out var number) && number > 0 ? number : throw new InvalidOperationException($"{name} must be a positive whole number.")
        : fallback;
}
