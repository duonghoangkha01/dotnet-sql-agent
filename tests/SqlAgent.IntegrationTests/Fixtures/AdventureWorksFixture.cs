using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using SqlAgent.Core;
using SqlAgent.Core.Execution;
using SqlAgent.Core.Schema;
using DotNet.Testcontainers.Configurations;
using Testcontainers.MsSql;

namespace SqlAgent.IntegrationTests.Fixtures;

/// <summary>
/// One SQL Server for the whole test run: the image the local stack pins, AdventureWorks2022 restored once from a
/// cached .bak, then every numbered script of deploy/sql replayed in order (users, row-level security, grants,
/// settings). What the tests query is therefore the same database the compose stack bootstraps.
/// Set SQLAGENT_TEST_BAK to a file path to keep the ~200 MB backup between runs (CI caches it).
/// </summary>
public sealed class AdventureWorksFixture : IAsyncLifetime
{
    public const string CollectionName = "AdventureWorks database";
    public const string Database = "AdventureWorks2022";

    private static readonly IReadOnlyDictionary<Role, string> RolePasswords = new Dictionary<Role, string>
    {
        [Role.SalesRep] = "Sr-" + Guid.NewGuid().ToString("N") + "!a1",
        [Role.Finance] = "Fi-" + Guid.NewGuid().ToString("N") + "!a1",
        [Role.Admin] = "Ad-" + Guid.NewGuid().ToString("N") + "!a1",
    };

    private static readonly string AppPassword = "Ap-" + Guid.NewGuid().ToString("N") + "!a1";

    private MsSqlContainer? _container;

    public RoleConnectionFactory Connections { get; private set; } = null!;

    /// <summary>Connects as sa, which the row-level-security policy exempts: for computing expected counts and other setup.</summary>
    public string SaConnectionString { get; private set; } = null!;

    public string AppConnectionString { get; private set; } = null!;

    public SchemaCatalog Catalog { get; private set; } = null!;

    public AllowList AllowListFor(Role role) => Catalog.GetAllowList(role);

    public async Task InitializeAsync()
    {
        var backup = await EnsureBackupAsync();

        _container = new MsSqlBuilder(RepoFiles.SqlServerImage())
            .WithBindMount(backup, "/bak/AdventureWorks2022.bak", AccessMode.ReadOnly)
            .Build();
        await _container.StartAsync();

        var master = new SqlConnectionStringBuilder(_container.GetConnectionString()) { TrustServerCertificate = true };
        master.InitialCatalog = "master";
        SaConnectionString = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = Database }.ConnectionString;

        await RestoreAsync(master.ConnectionString);

        var variables = new Dictionary<string, string>
        {
            ["SQL_SALES_REP_PASSWORD"] = RolePasswords[Role.SalesRep],
            ["SQL_FINANCE_PASSWORD"] = RolePasswords[Role.Finance],
            ["SQL_ADMIN_PASSWORD"] = RolePasswords[Role.Admin],
            ["SQL_APP_PASSWORD"] = AppPassword,
        };
        foreach (var script in RepoFiles.NumberedSqlScripts())
        {
            await SqlScriptRunner.RunAsync(master.ConnectionString, script, variables);
        }

        var dataSource = $"{_container.Hostname},{_container.GetMappedPublicPort(1433)}";
        Connections = new RoleConnectionFactory(dataSource, Database, RolePasswords, trustServerCertificate: true);
        AppConnectionString = new SqlConnectionStringBuilder
        {
            DataSource = dataSource,
            InitialCatalog = Database,
            UserID = "sqlagent_app",
            Password = AppPassword,
            TrustServerCertificate = true,
        }.ConnectionString;

        try
        {
            // Also proves that the shipped semantic.yaml is valid against the real schema.
            Catalog = await SemanticLayerLoader.LoadAsync(new AgentAssetsOptions(), AppConnectionString);
        }
        catch (SqlException ex) when (ex.Number == 18456)
        {
            // "Login failed" hides its reason from the client; the server's error log has it.
            throw new InvalidOperationException("sqlagent_app could not log in. Server log: " + await LoginFailuresAsync(), ex);
        }
    }

    private async Task<string> LoginFailuresAsync()
    {
        await using var connection = new SqlConnection(SaConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("EXEC sp_readerrorlog 0, 1, N'Login failed'", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(2));
        return string.Join(" | ", lines);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>Runs a scalar query as sa, bypassing row-level security.</summary>
    public async Task<T> ScalarAsSaAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(SaConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (T)Convert.ChangeType(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("NULL: " + sql), typeof(T));
    }

    private async Task RestoreAsync(string masterConnectionString)
    {
        await using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // Same logical names and target files as deploy/sql/entrypoint.sh.
        command.CommandText = "RESTORE DATABASE AdventureWorks2022 FROM DISK = N'/bak/AdventureWorks2022.bak' " +
                              "WITH MOVE N'AdventureWorks2022' TO N'/var/opt/mssql/data/AdventureWorks2022.mdf', " +
                              "MOVE N'AdventureWorks2022_log' TO N'/var/opt/mssql/data/AdventureWorks2022_log.ldf', REPLACE";
        command.CommandTimeout = 900;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> EnsureBackupAsync()
    {
        var (url, sha256) = RepoFiles.AdventureWorksBackup();
        var path = Environment.GetEnvironmentVariable("SQLAGENT_TEST_BAK")
                   ?? Path.Combine(Path.GetTempPath(), "sqlagent-test-cache", "AdventureWorks2022.bak");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path) && await Sha256Async(path) == sha256) return path;

        var partial = path + ".part";
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) })
        await using (var source = await http.GetStreamAsync(url))
        await using (var target = File.Create(partial))
        {
            await source.CopyToAsync(target);
        }

        if (await Sha256Async(partial) != sha256)
        {
            File.Delete(partial);
            throw new InvalidOperationException("The downloaded AdventureWorks2022.bak does not match the SHA-256 pinned in deploy/fetch-bak.sh.");
        }

        File.Move(partial, path, overwrite: true);
        return path;
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }
}

[CollectionDefinition(AdventureWorksFixture.CollectionName)]
public sealed class AdventureWorksCollection : ICollectionFixture<AdventureWorksFixture>;
