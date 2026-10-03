namespace SqlAgent.Core.Guardrails;

/// <summary>Why the guardrail refused a query. Stable identifiers: the adversarial corpus asserts on them.</summary>
public enum ViolationCode
{
    /// <summary>Longer than the size limit, or nested so deeply that parsing it could exhaust the stack.</summary>
    TooComplex,
    ParseError,
    /// <summary>More than one statement or batch (stacked statements, <c>GO</c>).</summary>
    MultipleStatements,
    /// <summary>One statement, but not a SELECT (DML, DDL, EXEC, WAITFOR, DECLARE, ...).</summary>
    NotASelect,
    SelectInto,
    /// <summary>FOR XML / FOR JSON / other FOR clauses.</summary>
    ForClause,
    /// <summary>Table hints, join hints, OPTION (...) and COMPUTE.</summary>
    QueryHint,
    /// <summary>A FROM item that is not a plain table, derived table or join (functions, OPENROWSET, ...).</summary>
    ForbiddenTableSource,
    /// <summary>A 2-part table name outside the role's allowlist, or a 1-part name that is not a CTE in scope.</summary>
    TableNotAllowed,
    /// <summary>A 3- or 4-part name (another database or server).</summary>
    CrossDatabaseName,
    ForbiddenFunction,
    /// <summary>A scalar or boolean expression node outside the allowlist (variables, @@ globals, NEXT VALUE FOR, ...).</summary>
    ForbiddenExpression,
    /// <summary><c>SELECT *</c>, <c>t.*</c>, or <c>*</c> inside anything but <c>COUNT(*)</c>.</summary>
    WildcardNotAllowed,
    DeniedColumn,
    /// <summary>A column qualifier that matches no table or alias in scope (for example a method or property access).</summary>
    UnknownColumnQualifier,
    /// <summary>Any other syntax element outside the allowlist.</summary>
    ForbiddenConstruct,
}

/// <param name="Message">Written for the model to correct itself. Names only what the query itself contained.</param>
public sealed record Violation(ViolationCode Code, string Message);

/// <param name="Sql">The SQL to run: regenerated from the validated tree (so what was checked is what runs),
/// with <c>TOP (MaxRows + 1)</c> added where the outermost query allows it. Null when the query was refused.</param>
public sealed record GuardrailResult(bool Allowed, string? Sql, IReadOnlyList<Violation> Violations)
{
    public static GuardrailResult Allow(string sql) => new(true, sql, []);

    public static GuardrailResult Refuse(IEnumerable<Violation> violations) => new(false, null, violations.ToList());

    public static GuardrailResult Refuse(ViolationCode code, string message) => Refuse([new Violation(code, message)]);
}
