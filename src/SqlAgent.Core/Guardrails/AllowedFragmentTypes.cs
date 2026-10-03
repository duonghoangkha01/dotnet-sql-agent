using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlAgent.Core.Guardrails;

/// <summary>
/// Every syntax-tree node type a generated query may contain. The guardrail is default-deny: a node whose type is
/// not listed here is refused, so a construct nobody thought about (a rowset function, a new T-SQL feature added
/// by a ScriptDom update) fails closed instead of slipping through. Nodes that need more than a type check
/// (functions, column references, table names) are inspected further by <see cref="GuardrailVisitor"/>.
/// </summary>
internal static class AllowedFragmentTypes
{
    private static readonly HashSet<Type> Allowed =
    [
        // Query structure
        typeof(SelectStatement), typeof(QuerySpecification), typeof(BinaryQueryExpression),
        typeof(QueryParenthesisExpression), typeof(WithCtesAndXmlNamespaces), typeof(CommonTableExpression),
        typeof(SelectScalarExpression), typeof(SelectStarExpression), typeof(FromClause), typeof(WhereClause),
        typeof(HavingClause), typeof(GroupByClause), typeof(ExpressionGroupingSpecification),
        typeof(GroupingSetsGroupingSpecification), typeof(CompositeGroupingSpecification),
        typeof(RollupGroupingSpecification), typeof(CubeGroupingSpecification), typeof(GrandTotalGroupingSpecification),
        typeof(OrderByClause), typeof(ExpressionWithSortOrder), typeof(TopRowFilter), typeof(OffsetClause),
        typeof(OverClause), typeof(WindowFrameClause), typeof(WindowDelimiter), typeof(WithinGroupClause),
        typeof(SearchedWhenClause), typeof(SimpleWhenClause),
        typeof(Identifier), typeof(MultiPartIdentifier), typeof(SchemaObjectName), typeof(IdentifierOrValueExpression),
        typeof(SqlDataTypeReference),

        // Table sources
        typeof(NamedTableReference), typeof(QueryDerivedTable), typeof(QualifiedJoin), typeof(UnqualifiedJoin),
        typeof(JoinParenthesisTableReference),

        // Scalar expressions
        typeof(ColumnReferenceExpression), typeof(IntegerLiteral), typeof(NumericLiteral), typeof(RealLiteral),
        typeof(StringLiteral), typeof(MoneyLiteral), typeof(BinaryLiteral), typeof(NullLiteral), typeof(MaxLiteral),
        typeof(IdentifierLiteral), // the datepart keyword in DATEADD(month, ...)
        typeof(BinaryExpression), typeof(UnaryExpression), typeof(ParenthesisExpression),
        typeof(SearchedCaseExpression), typeof(SimpleCaseExpression),
        typeof(CastCall), typeof(ConvertCall), typeof(TryCastCall), typeof(TryConvertCall),
        typeof(CoalesceExpression), typeof(NullIfExpression), typeof(IIfCall),
        typeof(FunctionCall), typeof(LeftFunctionCall), typeof(RightFunctionCall),
        typeof(ParameterlessCall), typeof(ScalarSubquery),

        // Boolean expressions
        typeof(BooleanComparisonExpression), typeof(BooleanBinaryExpression), typeof(BooleanNotExpression),
        typeof(BooleanParenthesisExpression), typeof(BooleanIsNullExpression), typeof(BooleanTernaryExpression),
        typeof(InPredicate), typeof(LikePredicate), typeof(ExistsPredicate), typeof(SubqueryComparisonPredicate),
        typeof(DistinctPredicate),
    ];

    public static bool Contains(Type type) => Allowed.Contains(type);
}
