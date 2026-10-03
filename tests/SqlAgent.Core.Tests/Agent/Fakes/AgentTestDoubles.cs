using SqlAgent.Core.Agent;
using SqlAgent.Core.Execution;
using SqlAgent.Core.Governance;
using SqlAgent.Core.Schema;

namespace SqlAgent.Core.Tests.Agent.Fakes;

/// <summary>Keeps every event in order. Linked into the integration tests as well.</summary>
public sealed class ListEventSink : IEventSink
{
    private readonly List<SseEvent> _events = [];

    public IReadOnlyList<SseEvent> Events
    {
        get { lock (_events) return _events.ToList(); }
    }

    public IEnumerable<T> Of<T>() where T : SseEvent => Events.OfType<T>();

    public ValueTask EmitAsync(SseEvent evt, CancellationToken ct = default)
    {
        lock (_events) _events.Add(evt);
        return ValueTask.CompletedTask;
    }
}

public sealed class RecordingAuditSink : IAuditSink
{
    private readonly List<AuditEntry> _entries = [];

    public IReadOnlyList<AuditEntry> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    public ValueTask WriteAsync(AuditEntry entry)
    {
        lock (_entries) _entries.Add(entry);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Runs queries through a delegate instead of a database.</summary>
public sealed class FakeQueryExecutor(Func<string, UserContext, CancellationToken, Task<QueryResult>>? run = null) : IQueryExecutor
{
    private readonly List<(string Sql, UserContext User)> _calls = [];

    public IReadOnlyList<(string Sql, UserContext User)> Calls
    {
        get { lock (_calls) return _calls.ToList(); }
    }

    public Task<QueryResult> ExecuteAsync(string sql, UserContext user, CancellationToken ct = default)
    {
        lock (_calls) _calls.Add((sql, user));
        return (run ?? ((_, _, _) => Task.FromResult(Rows(3))))(sql, user, ct);
    }

    /// <summary>A result of <paramref name="count"/> rows: a numeric <c>Id</c> and a text <c>Name</c>.</summary>
    public static QueryResult Rows(int count, bool truncated = false) => new(
        [new QueryColumn("Id", "int"), new QueryColumn("Name", "nvarchar")],
        Enumerable.Range(1, count).Select(i => new object?[] { i, "name" + i }).ToList(),
        truncated,
        ElapsedMs: 3);
}

/// <summary>A small fixed catalog, so agent tests need neither a database nor semantic.yaml.</summary>
public sealed class StubSchemaCatalog : ISchemaCatalog
{
    private static AllowList Allow(params string[] tables) => new(
        new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase));

    private static readonly IReadOnlyDictionary<Role, string[]> Tables = new Dictionary<Role, string[]>
    {
        [Role.SalesRep] = ["Sales.SalesOrderHeader"],
        [Role.Finance] = ["Sales.SalesOrderHeader", "Sales.SalesTerritory"],
        [Role.Admin] = ["Sales.SalesOrderHeader", "Sales.SalesTerritory", "Person.Person"],
    };

    public IReadOnlyList<TableSummary> ListTables(Role role) =>
        Tables[role].Select(t => new TableSummary(t, "Test table " + t)).ToList();

    public DescribeTablesResult DescribeTables(Role role, IEnumerable<string> names)
    {
        var found = new List<TableDescription>();
        var errors = new List<string>();
        foreach (var name in names)
        {
            if (Tables[role].Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(new TableDescription(name, "Test table", [new ColumnDescription("Id", "int", false, null)], [],
                    new Dictionary<string, string>(), []));
            }
            else
            {
                errors.Add(SchemaCatalog.NotAvailableMessage(name));
            }
        }

        return new DescribeTablesResult(found, errors);
    }

    public AllowList GetAllowList(Role role) => Allow(Tables[role]);

    public IReadOnlyList<SqlExample> GetExamples(Role role) =>
        [new SqlExample([role], "How many orders are there?", "SELECT COUNT(*) AS Orders FROM Sales.SalesOrderHeader")];
}

public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _now.UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by) => _now += by;
}
