namespace SqlAgent.Core.Guardrails;

/// <summary>
/// The built-in functions a generated query may call: aggregates, window, string, logical, date and math.
/// Anything else is refused, which keeps out user-defined functions, system functions that read server state
/// (<c>USER_NAME</c>, <c>SESSION_CONTEXT</c>, <c>DB_NAME</c>, <c>OBJECT_ID</c>, ...), and side-effecting ones (<c>NEWID</c>, <c>NEXT VALUE FOR</c>).
/// Only unqualified calls are considered: a call with a schema or type target is refused before this list is asked.
/// </summary>
public static class FunctionAllowList
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        // Aggregates
        "AVG", "COUNT", "COUNT_BIG", "MAX", "MIN", "SUM", "STDEV", "STDEVP", "VAR", "VARP", "STRING_AGG",
        "GROUPING", "GROUPING_ID", "APPROX_COUNT_DISTINCT",
        // Window
        "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE", "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE",
        "CUME_DIST", "PERCENT_RANK", "PERCENTILE_CONT", "PERCENTILE_DISC",
        // String
        "ASCII", "CHAR", "CHARINDEX", "CONCAT", "CONCAT_WS", "DIFFERENCE", "LEN", "LOWER", "LTRIM", "NCHAR",
        "PATINDEX", "REPLACE", "REPLICATE", "REVERSE", "RTRIM", "SOUNDEX", "SPACE", "STR", "STUFF", "SUBSTRING",
        "TRANSLATE", "TRIM", "UNICODE", "UPPER",
        // Logical
        "ISNULL", "CHOOSE", "GREATEST", "LEAST", "ISNUMERIC", "ISDATE",
        // Date and time
        "DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATEFROMPARTS", "DATENAME", "DATEPART", "DATETIME2FROMPARTS",
        "DATETIMEFROMPARTS", "DATETRUNC", "DATE_BUCKET", "DAY", "EOMONTH", "GETDATE", "GETUTCDATE", "MONTH",
        "SYSDATETIME", "SYSUTCDATETIME", "SYSDATETIMEOFFSET", "TIMEFROMPARTS", "YEAR",
        // Math
        "ABS", "ACOS", "ASIN", "ATAN", "ATN2", "CEILING", "COS", "COT", "DEGREES", "EXP", "FLOOR", "LOG", "LOG10",
        "PI", "POWER", "RADIANS", "ROUND", "SIGN", "SIN", "SQRT", "SQUARE", "TAN",
    };

    public static bool Contains(string functionName) => Names.Contains(functionName);
}
