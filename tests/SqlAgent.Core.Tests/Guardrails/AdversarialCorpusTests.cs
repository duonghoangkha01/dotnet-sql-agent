using System.Text.Json;
using SqlAgent.Core.Guardrails;
using SqlAgent.Core.Schema;

namespace SqlAgent.Core.Tests.Guardrails;

/// <summary>
/// Every case in adversarial-corpus.json must be refused, with the violation code the case names. Cases are
/// only ever added: when a valid query is over-blocked, the fix goes into ValidQueriesTests, not into this file.
/// </summary>
public class AdversarialCorpusTests
{
    private static readonly SqlGuardrail Guardrail = new();

    public sealed record CorpusCase(string Name, string Sql, string Expect, string? Role);

    private static readonly IReadOnlyList<CorpusCase> Corpus = JsonSerializer.Deserialize<List<CorpusCase>>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Guardrails", "adversarial-corpus.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    public static TheoryData<string> CaseNames => new(Corpus.Select(c => c.Name));

    private static CorpusCase Get(string name) => Corpus.Single(c => c.Name == name);

    private static Role RoleOf(CorpusCase c) => c.Role is null ? Role.SalesRep : Enum.Parse<Role>(c.Role.Replace("_", ""), ignoreCase: true);

    [Fact]
    public void Corpus_has_at_least_45_cases_with_unique_names_and_known_codes()
    {
        Assert.True(Corpus.Count >= 45, $"only {Corpus.Count} cases");
        Assert.Equal(Corpus.Count, Corpus.Select(c => c.Name).Distinct().Count());
        Assert.All(Corpus, c => Assert.True(Enum.TryParse<ViolationCode>(c.Expect, out _), $"{c.Name}: unknown code {c.Expect}"));
    }

    [Fact]
    public void Every_violation_code_that_matters_is_exercised()
    {
        var covered = Corpus.Select(c => Enum.Parse<ViolationCode>(c.Expect)).ToHashSet();
        // TooComplex is exercised by GuardrailBehaviorTests, which builds its inputs in code.
        var missing = Enum.GetValues<ViolationCode>().Where(code => code != ViolationCode.TooComplex && !covered.Contains(code));
        Assert.Empty(missing);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Case_is_refused_with_the_expected_code(string name)
    {
        var c = Get(name);
        var result = Guardrail.Validate(c.Sql, GuardrailTestData.AllowList(RoleOf(c)));

        Assert.False(result.Allowed, $"{c.Name} was allowed as: {result.Sql}");
        Assert.Null(result.Sql);
        Assert.Contains(result.Violations, v => v.Code.ToString() == c.Expect);
    }

    /// <summary>
    /// A refusal must never confirm that something exists: a message may name only what the query itself
    /// contained. Checked against every table and denied column the role cannot use.
    /// </summary>
    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Messages_name_nothing_that_the_query_did_not_contain(string name)
    {
        var c = Get(name);
        var role = RoleOf(c);
        var allow = GuardrailTestData.AllowList(role);
        var result = Guardrail.Validate(c.Sql, allow);

        var secrets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var other in RoleExtensions.All)
        {
            foreach (var table in GuardrailTestData.AllowList(other).Tables.Where(t => !allow.Tables.Contains(t)))
            {
                secrets.Add(table);
                secrets.Add(table[(table.IndexOf('.') + 1)..]);
            }
        }

        foreach (var column in allow.DeniedColumns.Values.SelectMany(cols => cols)) secrets.Add(column);
        foreach (var column in GuardrailTestData.AllowList(Role.Admin).DeniedColumns.Values.SelectMany(cols => cols)) secrets.Add(column);

        // Messages quote names in normalized form: without brackets, quotes and padding spaces.
        var flatSql = Flatten(c.Sql);

        foreach (var violation in result.Violations)
        {
            foreach (var secret in secrets.Where(s => !flatSql.Contains(s, StringComparison.OrdinalIgnoreCase)))
            {
                Assert.False(
                    violation.Message.Contains(secret, StringComparison.OrdinalIgnoreCase),
                    $"{c.Name}: message leaks '{secret}': {violation.Message}");
            }
        }
    }

    private static string Flatten(string sql) => new(sql.Where(ch => ch is not ('[' or ']' or '"') && !char.IsWhiteSpace(ch)).ToArray());

    [Fact]
    public void Unavailable_table_and_missing_table_get_the_same_reply()
    {
        var allow = GuardrailTestData.AllowList(Role.SalesRep);
        var denied = Guardrail.Validate("SELECT JobTitle FROM HumanResources.Employee", allow);
        var missing = Guardrail.Validate("SELECT JobTitle FROM HumanResources.Nonexistent", allow);

        Assert.Equal(
            denied.Violations.Select(v => v.Message.Replace("HumanResources.Employee", "X")),
            missing.Violations.Select(v => v.Message.Replace("HumanResources.Nonexistent", "X")));
    }
}
