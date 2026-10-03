using SqlAgent.Cli.Evals;
using SqlAgent.Core;
using SqlAgent.Core.Agent;
using SqlAgent.Core.Execution;
using SqlAgent.Core.Governance;
using SqlAgent.Core.Guardrails;
using SqlAgent.IntegrationTests.Fixtures;
using SqlAgent.Core.Tests.Agent.Fakes;
using Xunit.Abstractions;

namespace SqlAgent.IntegrationTests;

/// <summary>
/// Every golden reference query, run as its persona's own database user against the real AdventureWorks: a reference
/// can therefore never hold data its persona may not see, and every one is known to run, to return something, and to fit
/// the eval executor's row cap. They are also checked against the guardrail, so the golden set measures the agent and not a
/// reference the guardrail would refuse.
/// </summary>
[Collection(AdventureWorksFixture.CollectionName)]
public class GoldenSetTests(AdventureWorksFixture db, ITestOutputHelper output)
{
    private static readonly QueryLimits EvalLimits = new(MaxRows: GoldenSetLoader.MaxReferenceRows, MaxResultBytes: 256L * 1024 * 1024);

    public static IEnumerable<object[]> Items() =>
        GoldenSetLoader.LoadFile(Path.Combine(RepoFiles.Root, "evals", "golden.json")).Select(i => new object[] { i.Id });

    [Theory]
    [MemberData(nameof(Items))]
    public async Task The_reference_query_passes_the_guardrail_and_runs_as_its_persona(string id)
    {
        var item = GoldenSetLoader.LoadFile(Path.Combine(RepoFiles.Root, "evals", "golden.json")).Single(i => i.Id == id);
        var user = item.User;

        var verdict = new SqlGuardrail(EvalLimits).Validate(item.ReferenceSql, db.AllowListFor(user.Role));
        Assert.True(verdict.Allowed, "refused by the guardrail: " + string.Join(" | ", verdict.Violations.Select(v => v.Message)));

        var result = await new SafeQueryExecutor(db.Connections, EvalLimits).ExecuteAsync(item.ReferenceSql, user);

        output.WriteLine($"{item.Id}: {result.RowCount} rows, columns {string.Join(", ", result.Columns.Select(c => c.Name))}");
        foreach (var row in result.Rows.Take(12)) output.WriteLine("  " + string.Join(" | ", row.Select(v => v ?? "NULL")));

        Assert.False(result.Truncated, "the reference result exceeds the eval row cap");
        Assert.True(result.RowCount > 0, "the reference result is empty, so it cannot tell a right answer from a wrong one");
    }

    /// <summary>
    /// The whole scoring path on real SQL Server values (decimal, datetime, bit, text): a model that writes exactly the
    /// reference query must pass every item. If it did not, the comparer or the runner would be failing right answers.
    /// </summary>
    [Fact]
    public async Task A_model_that_writes_the_reference_query_passes_every_item()
    {
        var items = GoldenSetLoader.LoadFile(Path.Combine(RepoFiles.Root, "evals", "golden.json"));
        var guardrail = new SqlGuardrail(EvalLimits);
        var executor = new SafeQueryExecutor(db.Connections, EvalLimits);
        var llm = new LlmOptions();
        var failures = new List<string>();

        foreach (var item in items)
        {
            var model = new ScriptedChatClient(ScriptedChatClient.RunSql(item.ReferenceSql), ScriptedChatClient.Reply("Here is the result."));
            var registry = new RoleAgentRegistry(ChatClientFactory.Wrap(model, llm), db.Catalog, "SYSTEM PROMPT", llm);
            var turns = new AgentTurnRunner(registry, db.Catalog, guardrail, executor, NullAuditSink.Instance, new AgentOptions());
            using var runner = new EvalRunner(turns, db.Catalog, guardrail, executor, llm.MaxIterations);

            var result = await runner.RunAsync(item);
            if (!result.Passed) failures.Add($"{item.Id}: {result.Verdict} {result.Detail}");
            Assert.False(result.GuardrailFalsePositive, item.Id);
        }

        Assert.Empty(failures);
    }
}
