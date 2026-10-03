using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SqlAgent.Core;
using SqlAgent.IntegrationTests.Fixtures;

namespace SqlAgent.IntegrationTests;

/// <summary>
/// deploy/sql/45-resource-governor.sql: the database itself caps a reader's query (parallelism, CPU time, memory
/// grant), independently of the guardrail and of the executor's own timeout. Queries here go straight to the
/// database, as they would if the other two layers failed.
/// </summary>
[Collection(AdventureWorksFixture.CollectionName)]
public class ResourceGovernorTests(AdventureWorksFixture db)
{
    // 121k x 121k x 121k rows: it cannot finish, so only a limit can end it.
    private const string RunawayQuery =
        "SELECT COUNT_BIG(*) FROM Sales.SalesOrderDetail AS a CROSS JOIN Sales.SalesOrderDetail AS b CROSS JOIN Sales.SalesOrderDetail AS c";

    private const string WorkloadGroup = "sqlagent_readers";

    [Fact]
    public async Task The_workload_group_carries_the_configured_limits()
    {
        var limits = await db.ScalarAsSaAsync<string>(
            "SELECT CONCAT(request_max_memory_grant_percent, '/', request_max_cpu_time_sec, '/', max_dop) " +
            $"FROM sys.resource_governor_workload_groups WHERE name = N'{WorkloadGroup}'");

        Assert.Equal("10/10/1", limits);
    }

    [Theory]
    [InlineData(Role.SalesRep)]
    [InlineData(Role.Finance)]
    [InlineData(Role.Admin)]
    public async Task Every_reader_session_lands_in_the_group_and_other_logins_do_not(Role role)
    {
        await using var reader = await db.Connections.OpenAsync(role, default);
        var readerSpid = (short)(await new SqlCommand("SELECT @@SPID", reader).ExecuteScalarAsync())!;

        Assert.Equal(WorkloadGroup, await GroupOfSessionAsync(readerSpid));

        await using var sa = new SqlConnection(db.SaConnectionString);
        await sa.OpenAsync();
        var saSpid = (short)(await new SqlCommand("SELECT @@SPID", sa).ExecuteScalarAsync())!;
        Assert.Equal("default", await GroupOfSessionAsync(saSpid));
    }

    [Fact]
    public async Task A_runaway_query_is_aborted_by_the_database_before_the_client_gives_up()
    {
        var stopwatch = Stopwatch.StartNew();

        // A 90 s client timeout: only the database's own limit can end this within the bounds asserted below.
        var ex = await Assert.ThrowsAsync<SqlException>(() => RunAsRoleAsync(Role.Admin, RunawayQuery, timeoutSeconds: 90));

        Assert.NotEqual(-2, ex.Number); // not the client's timeout
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 8, 40);
    }

    [Fact]
    public async Task A_maxdop_hint_cannot_raise_the_parallelism_of_a_reader()
    {
        var running = RunAsRoleAsync(Role.Admin, RunawayQuery + " OPTION (MAXDOP 8)", timeoutSeconds: 90);
        var observed = new List<int>();

        while (!running.IsCompleted)
        {
            var dop = await db.ScalarAsSaAsync<int>(
                "SELECT ISNULL(MAX(r.dop), 0) FROM sys.dm_exec_requests AS r JOIN sys.dm_exec_sessions AS s ON s.session_id = r.session_id " +
                "WHERE s.login_name = N'sqlagent_admin' AND r.session_id <> @@SPID");
            if (dop > 0) observed.Add(dop);
            await Task.Delay(300);
        }

        await Assert.ThrowsAsync<SqlException>(() => running); // the runaway is still cut off
        Assert.NotEmpty(observed);
        Assert.All(observed, dop => Assert.Equal(1, dop));
    }

    private async Task<string> GroupOfSessionAsync(short spid) => await db.ScalarAsSaAsync<string>(
        "SELECT g.name FROM sys.dm_exec_sessions AS s " +
        "JOIN sys.dm_resource_governor_workload_groups AS g ON g.group_id = s.group_id " +
        $"WHERE s.session_id = {spid}");

    private async Task RunAsRoleAsync(Role role, string sql, int timeoutSeconds)
    {
        await using var connection = await db.Connections.OpenAsync(role, default);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = timeoutSeconds };
        await command.ExecuteScalarAsync();
    }
}
