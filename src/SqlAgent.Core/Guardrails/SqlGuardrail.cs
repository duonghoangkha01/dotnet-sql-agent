using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlAgent.Core.Agent;
using SqlAgent.Core.Schema;

namespace SqlAgent.Core.Guardrails;

/// <summary>
/// Default-deny validator for model-written T-SQL: parse with ScriptDom, insist on one SELECT, refuse every
/// construct not explicitly allowed, then run the SQL regenerated from the validated tree. It is one of three
/// independent layers; the role's database user and row-level security still apply to whatever passes.
/// Stateless and thread-safe.
/// </summary>
public sealed class SqlGuardrail(QueryLimits? limits = null)
{
    // ScriptDom recurses per nesting level and a stack overflow cannot be caught. Two shapes crash it on a 1 MB
    // stack: deep parenthesization (~1,000 levels) and a long flat chain of binary operators or CASE expressions
    // (empirically ~10,000 operators, ~20,000 characters). MaxNestingDepth guards the first shape directly;
    // MaxSqlLength is what actually guards the second (at 5,000 characters, under a quarter of that chain's
    // crash length) — a flat chain is only ~40 parentheses deep, so NestingDepth alone would miss it.
    // Analytical queries need neither shape, so both limits are generous for legitimate SQL and tight against this.
    public const int MaxSqlLength = 5000;
    public const int MaxNestingDepth = 40;

    private readonly QueryLimits _limits = limits ?? new QueryLimits();

    private static readonly SqlScriptGeneratorOptions GeneratorOptions = new()
    {
        KeywordCasing = KeywordCasing.Uppercase,
        AlignClauseBodies = false,
    };

    public GuardrailResult Validate(string? sql, AllowList allow)
    {
        // Tags hold the verdict and violation codes only, never the SQL text.
        using var span = AgentTelemetry.Source.StartActivity("guardrail.validate");
        var result = ValidateCore(sql, allow);
        span?.SetTag("allowed", result.Allowed);
        span?.SetTag("violation_codes", string.Join(',', result.Violations.Select(v => v.Code).Distinct()));
        return result;
    }

    private GuardrailResult ValidateCore(string? sql, AllowList allow)
    {
        var first = Analyze(sql, allow, enforceSizeLimits: true);
        if (first.Refusal is { } refusal) return refusal;

        var select = first.Select!;
        InjectRowCap(select);
        new Sql160ScriptGenerator(GeneratorOptions).GenerateScript(select, out var rewritten);

        // What runs is the regenerated text, so it is checked as well: a generator bug must fail closed.
        var second = Analyze(rewritten, allow, enforceSizeLimits: false);
        return second.Refusal is null
            ? GuardrailResult.Allow(rewritten)
            : GuardrailResult.Refuse(ViolationCode.ForbiddenConstruct, "The query could not be normalized safely. Rewrite it more simply.");
    }

    private (GuardrailResult? Refusal, SelectStatement? Select) Analyze(string? sql, AllowList allow, bool enforceSizeLimits)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return (GuardrailResult.Refuse(ViolationCode.NotASelect, "Provide exactly one SELECT statement."), null);

        // The regenerated text is longer than what was submitted, and has the nesting that already parsed once.
        if (enforceSizeLimits && (sql.Length > MaxSqlLength || NestingDepth(sql) > MaxNestingDepth))
            return (GuardrailResult.Refuse(ViolationCode.TooComplex,
                $"The query is too long or too deeply nested (limit {MaxSqlLength} characters, {MaxNestingDepth} levels of parentheses). Simplify it."), null);

        // QUOTED_IDENTIFIER ON, as the executor's connection runs, so "Col" parses as an identifier here as on the server.
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var script = (TSqlScript)parser.Parse(reader, out var errors);
        if (errors.Count > 0)
        {
            var details = string.Join(" ", errors.Take(3).Select(e => $"{e.Message} (line {e.Line})"));
            return (GuardrailResult.Refuse(ViolationCode.ParseError, $"The SQL does not parse: {details}"), null);
        }

        var statements = script.Batches.SelectMany(b => b.Statements).ToList();
        if (statements.Count > 1 || script.Batches.Count > 1)
            return (GuardrailResult.Refuse(ViolationCode.MultipleStatements, "Exactly one statement is allowed; remove the extra statements and any GO."), null);

        if (statements is not [SelectStatement select])
            return (GuardrailResult.Refuse(ViolationCode.NotASelect, "Only a single SELECT statement is allowed (a CTE may precede it)."), null);

        var structural = new List<Violation>();
        if (select.Into is not null)
            structural.Add(new(ViolationCode.SelectInto, "SELECT ... INTO is not allowed; queries may only read."));
        if (select.OptimizerHints.Count > 0)
            structural.Add(new(ViolationCode.QueryHint, "Query hints (OPTION (...)) are not allowed."));
        if (select.ComputeClauses.Count > 0)
            structural.Add(new(ViolationCode.ForbiddenConstruct, "COMPUTE clauses are not allowed."));
        if (structural.Count > 0) return (GuardrailResult.Refuse(structural), null);

        var visitor = new GuardrailVisitor(allow);
        select.Accept(visitor);
        return visitor.Violations.Count > 0 ? (GuardrailResult.Refuse(visitor.Violations), null) : (null, select);
    }

    /// <summary>
    /// Adds TOP (MaxRows + 1) to a plain outermost SELECT so the server stops early; the extra row tells the
    /// executor the result was cut. UNION and other set operations, and queries that already have TOP or OFFSET,
    /// are left alone: the executor's row cap covers them.
    /// </summary>
    private void InjectRowCap(SelectStatement select)
    {
        if (select.QueryExpression is not QuerySpecification { TopRowFilter: null, OffsetClause: null } query) return;

        query.TopRowFilter = new TopRowFilter
        {
            Expression = new ParenthesisExpression
            {
                Expression = new IntegerLiteral { Value = (_limits.MaxRows + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) },
            },
        };
    }

    /// <summary>Deepest run of open parentheses. It also counts those inside string literals: only ever too strict.</summary>
    private static int NestingDepth(string sql)
    {
        int depth = 0, max = 0;
        foreach (var ch in sql)
        {
            if (ch == '(') max = Math.Max(max, ++depth);
            else if (ch == ')' && depth > 0) depth--;
        }

        return max;
    }
}
