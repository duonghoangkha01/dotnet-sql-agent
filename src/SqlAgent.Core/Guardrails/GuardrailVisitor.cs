using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlAgent.Core.Schema;
using ScriptDomJoinHint = Microsoft.SqlServer.TransactSql.ScriptDom.JoinHint;

namespace SqlAgent.Core.Guardrails;

/// <summary>
/// Walks a parsed SELECT and records every violation. Default-deny: every node type must be listed in
/// <see cref="AllowedFragmentTypes"/>, and the few that carry meaning beyond their type (table names,
/// functions, column references, wildcards) get a further check.
/// </summary>
internal sealed partial class GuardrailVisitor(AllowList allow) : TSqlFragmentVisitor
{
    private readonly List<Violation> _violations = [];
    private readonly HashSet<Violation> _seen = [];

    // CTE names visible so far. A 1-part table name is accepted only if it is one of these: an unqualified name
    // that is not a CTE would resolve to an object in the user's default schema.
    private readonly HashSet<string> _visibleCtes = new(StringComparer.OrdinalIgnoreCase);

    // The only wildcards allowed: the argument of COUNT(*) / COUNT_BIG(*).
    private readonly HashSet<ColumnReferenceExpression> _countStars = [];

    private Scope? _scope;

    // Span of the last refused node. Its children are refused with it, and reporting each of them (an XML clause's
    // options, the parts of OPENDATASOURCE) would only bury the one message the model can act on.
    private int _blockedStart = -1, _blockedEnd = -1;

    public IReadOnlyList<Violation> Violations => _violations;

    // A subtree is walked parent-first, but ScriptDom lists a statement's WITH clause after its query. CTEs
    // must be known before the query that uses them, so the two are visited in that order here.
    public override void ExplicitVisit(SelectStatement node)
    {
        Visit((TSqlFragment)node);
        node.WithCtesAndXmlNamespaces?.Accept(this);
        node.QueryExpression?.Accept(this);
    }

    public override void ExplicitVisit(CommonTableExpression node)
    {
        _visibleCtes.Add(node.ExpressionName.Key());
        Visit((TSqlFragment)node);
        node.AcceptChildren(this);
    }

    // The scope is built up front: ScriptDom visits ORDER BY and the select list before FROM.
    public override void ExplicitVisit(QuerySpecification node)
    {
        var scope = Scope.Build(node, _scope);
        _scope = scope;
        Visit((TSqlFragment)node);
        node.AcceptChildren(this);
        _scope = scope.Parent;
    }

    public override void Visit(TSqlFragment node)
    {
        if (node.StartOffset >= _blockedStart && node.StartOffset < _blockedEnd) return;

        if (!AllowedFragmentTypes.Contains(node.GetType()))
        {
            FailUnlisted(node);
            _blockedStart = node.StartOffset;
            _blockedEnd = node.StartOffset + node.FragmentLength;
            return;
        }

        switch (node)
        {
            case NamedTableReference table:
                CheckTable(table);
                break;
            case QualifiedJoin { JoinHint: not ScriptDomJoinHint.None }:
                Fail(ViolationCode.QueryHint, "Join hints (LOOP, HASH, MERGE, ...) are not allowed.");
                break;
            case FunctionCall call:
                CheckFunction(call);
                break;
            case ParameterlessCall { ParameterlessCallType: not ParameterlessCallType.CurrentTimestamp }:
                Fail(ViolationCode.ForbiddenExpression, "USER, SYSTEM_USER and similar identity functions are not allowed.");
                break;
            case SelectStarExpression:
                Fail(ViolationCode.WildcardNotAllowed, "SELECT * and table.* are not allowed: list the columns explicitly.");
                break;
            case ColumnReferenceExpression column:
                CheckColumn(column);
                break;
        }
    }

    private void CheckTable(NamedTableReference table)
    {
        var name = table.SchemaObject;
        if (name.ServerIdentifier is not null || name.DatabaseIdentifier is not null)
        {
            Fail(ViolationCode.CrossDatabaseName, "Table names must be Schema.Table; other databases and servers are not available.");
        }
        else if (name.SchemaIdentifier is { } schema)
        {
            var full = schema.Key() + "." + name.BaseIdentifier.Key();
            if (!allow.Tables.Contains(full)) Fail(ViolationCode.TableNotAllowed, SchemaCatalog.NotAvailableMessage(full));
        }
        else if (!_visibleCtes.Contains(name.BaseIdentifier.Key()))
        {
            Fail(ViolationCode.TableNotAllowed,
                $"Table '{name.BaseIdentifier.Value}' must be written as Schema.Table unless it is a CTE defined earlier in this query. " +
                "Call list_tables to see the available tables.");
        }
    }

    private void CheckFunction(FunctionCall call)
    {
        var name = call.FunctionName.Value;
        if (call.CallTarget is not null)
        {
            Fail(ViolationCode.ForbiddenFunction,
                $"Qualified calls such as schema.function(), type::method() and column.method() are not allowed ('{name}').");
            return;
        }

        if (!FunctionAllowList.Contains(name))
        {
            Fail(ViolationCode.ForbiddenFunction,
                $"Function '{name}' is not allowed. Use standard aggregate, window, string, date and math functions.");
            return;
        }

        if ((name.Equals("COUNT", StringComparison.OrdinalIgnoreCase) || name.Equals("COUNT_BIG", StringComparison.OrdinalIgnoreCase))
            && call.Parameters is [ColumnReferenceExpression { ColumnType: ColumnType.Wildcard, MultiPartIdentifier: null or { Count: 0 } } star])
        {
            _countStars.Add(star);
        }
    }

    private void CheckColumn(ColumnReferenceExpression column)
    {
        if (column.ColumnType == ColumnType.Wildcard)
        {
            if (!_countStars.Contains(column))
                Fail(ViolationCode.WildcardNotAllowed, "'*' is only allowed as COUNT(*). List the columns explicitly.");
            return;
        }

        if (column.ColumnType != ColumnType.Regular)
        {
            Fail(ViolationCode.ForbiddenExpression, "Pseudo-columns such as $IDENTITY and $ROWGUID are not allowed.");
            return;
        }

        var identifiers = column.MultiPartIdentifier.Identifiers;
        var name = string.Join('.', identifiers.Select(i => i.Value));
        var result = _scope is not null
            ? _scope.Check(identifiers, allow.DeniedColumns)
            : identifiers.Count == 1 ? ColumnCheck.Ok : ColumnCheck.UnknownQualifier;

        switch (result)
        {
            case ColumnCheck.Denied:
                Fail(ViolationCode.DeniedColumn,
                    $"Column '{identifiers[^1].Value}' was not found or is not available. Call describe_tables to see the available columns.");
                break;
            case ColumnCheck.UnknownQualifier:
                Fail(ViolationCode.UnknownColumnQualifier,
                    $"'{name}' does not refer to a table or alias in this query. Qualify columns with a table alias from the FROM clause.");
                break;
        }
    }

    private void FailUnlisted(TSqlFragment node)
    {
        var type = node.GetType();
        var label = Friendly(type.Name);
        switch (node)
        {
            case ForClause:
                Fail(ViolationCode.ForClause, "FOR XML, FOR JSON and other FOR clauses are not allowed.");
                break;
            case TableHint or OptimizerHint:
                Fail(ViolationCode.QueryHint, "Table hints (WITH (...)) and query hints (OPTION (...)) are not allowed.");
                break;
            case VariableReference or GlobalVariableExpression:
                Fail(ViolationCode.ForbiddenExpression, "Variables and @@ system variables are not allowed.");
                break;
            case ScalarExpression or BooleanExpression:
                Fail(ViolationCode.ForbiddenExpression, $"{label} is not allowed in queries.");
                break;
            case TableReference:
                Fail(ViolationCode.ForbiddenTableSource,
                    $"{label} is not allowed as a table source. Use tables from list_tables, subqueries and joins only.");
                break;
            default:
                Fail(ViolationCode.ForbiddenConstruct, $"{label} is not allowed in queries.");
                break;
        }
    }

    private void Fail(ViolationCode code, string message)
    {
        var violation = new Violation(code, message);
        if (_seen.Add(violation)) _violations.Add(violation);
    }

    /// <summary>"OpenRowsetTableReference" → "Open rowset table reference".</summary>
    private static string Friendly(string typeName)
    {
        var words = PascalBoundary().Replace(typeName, " ").ToLowerInvariant();
        return char.ToUpperInvariant(words[0]) + words[1..];
    }

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex PascalBoundary();
}
