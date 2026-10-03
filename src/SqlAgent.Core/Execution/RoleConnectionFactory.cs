using Microsoft.Data.SqlClient;

namespace SqlAgent.Core.Execution;

/// <summary>
/// Opens connections as the database user of a role (<c>sqlagent_sales_rep</c>, <c>sqlagent_finance</c>,
/// <c>sqlagent_admin</c>). Those users hold only the grants generated from semantic.yaml, so the database
/// enforces the same allowlist as the guardrail.
/// </summary>
public sealed class RoleConnectionFactory
{
    private readonly IReadOnlyDictionary<Role, string> _connectionStrings;

    /// <param name="passwords">One password per role; a missing role is a configuration error.</param>
    /// <param name="trustServerCertificate">For the local container's self-signed certificate only.</param>
    public RoleConnectionFactory(
        string dataSource,
        string database,
        IReadOnlyDictionary<Role, string> passwords,
        bool trustServerCertificate = false)
    {
        var strings = new Dictionary<Role, string>();
        foreach (var role in RoleExtensions.All)
        {
            if (!passwords.TryGetValue(role, out var password) || string.IsNullOrEmpty(password))
                throw new ArgumentException($"No database password for role '{role.ToKey()}'.", nameof(passwords));

            strings[role] = new SqlConnectionStringBuilder
            {
                DataSource = dataSource,
                InitialCatalog = database,
                UserID = role.ToDbUser(),
                Password = password,
                TrustServerCertificate = trustServerCertificate,
                // One command at a time: with MARS, a second command could run on a session whose context
                // the first one relies on (row-level security reads SESSION_CONTEXT).
                MultipleActiveResultSets = false,
                ApplicationName = "sqlagent",
                PersistSecurityInfo = false,
            }.ConnectionString;
        }

        _connectionStrings = strings;
    }

    public string ConnectionStringFor(Role role) => _connectionStrings[role];

    public async Task<SqlConnection> OpenAsync(Role role, CancellationToken ct)
    {
        var connection = new SqlConnection(_connectionStrings[role]);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
