using System.Globalization;
using SqlAgent.Core.Execution;

namespace SqlAgent.Core.Evals;

/// <param name="Match">Whether the agent's result counts as the reference result.</param>
/// <param name="Reason">Why not, in a form that goes into the eval report. Empty on a match.</param>
public sealed record ComparisonResult(bool Match, string Reason)
{
    public static readonly ComparisonResult Matched = new(true, "");

    public static ComparisonResult Mismatch(string reason) => new(false, reason);
}

/// <summary>
/// The execution-accuracy rule of the evals (documented in evals/README.md). Whether the agent's query is "right" is
/// decided on the values it returned, not on its text and not on its column names or order:
/// <list type="number">
/// <item>The row counts are equal.</item>
/// <item>Every reference column is matched to a distinct agent column holding the same multiset of values. Extra
/// agent columns are fine. Matching is greedy over the columns whose values are equal, so aliases and column order
/// do not matter.</item>
/// <item>The matched columns must also agree row by row: the same combinations of values, not just the same values
/// per column (a result that pairs names with the wrong totals fails).</item>
/// <item>Row order counts only when the reference is <c>ordered</c>.</item>
/// </list>
/// Values are compared as: numbers within <see cref="NumericTolerance"/> (any numeric type, booleans as 0/1), dates and
/// times by their value whatever the SQL type (a <c>date</c> equals a <c>datetime</c> at midnight), text exactly except
/// for trailing spaces, and NULL equal to NULL only. A number is never equal to text that looks like it.
/// </summary>
public static class ResultSetComparer
{
    public const double NumericTolerance = 1e-6;

    /// <summary>Column-assignment attempts before giving up: only columns holding identical values can be swapped, so this is never reached in practice.</summary>
    private const int MaxAssignmentAttempts = 256;

    public static ComparisonResult Compare(QueryResult reference, QueryResult actual, bool ordered)
    {
        if (reference.RowCount != actual.RowCount)
            return ComparisonResult.Mismatch($"row count differs: expected {reference.RowCount}, got {actual.RowCount}");

        var expectedColumns = Columnize(reference);
        var actualColumns = Columnize(actual);
        var sortedActual = actualColumns.Select(SortedCopy).ToArray();

        // For each reference column, the agent columns holding the same multiset of values.
        var candidates = new List<int>[expectedColumns.Length];
        for (var i = 0; i < expectedColumns.Length; i++)
        {
            var expected = SortedCopy(expectedColumns[i]);
            candidates[i] = Enumerable.Range(0, sortedActual.Length).Where(j => SameValues(expected, sortedActual[j])).ToList();
            if (candidates[i].Count == 0)
                return ComparisonResult.Mismatch($"no column of the answer holds the values of reference column '{reference.Columns[i].Name}'");
        }

        var attempts = 0;
        var assignment = new int[expectedColumns.Length];
        var used = new bool[actualColumns.Length];

        bool Assign(int column)
        {
            // Every entry counts, not just complete assignments, so dead ends cannot run away either.
            if (++attempts > MaxAssignmentAttempts) return false;
            if (column == expectedColumns.Length)
            {
                return RowsAgree(expectedColumns, actualColumns, assignment, reference.RowCount, ordered);
            }

            foreach (var candidate in candidates[column])
            {
                if (used[candidate]) continue;
                used[candidate] = true;
                assignment[column] = candidate;
                if (Assign(column + 1)) return true;
                used[candidate] = false;
            }

            return false;
        }

        if (Assign(0)) return ComparisonResult.Matched;
        if (attempts > MaxAssignmentAttempts)
            return ComparisonResult.Mismatch("too many columns with identical values to pair up; the result was not matched");

        // Either two reference columns need the same agent column, or the rows did not line up.
        if (!CanAssignDistinct(candidates, actualColumns.Length))
            return ComparisonResult.Mismatch("the answer has fewer distinct matching columns than the reference");

        return ComparisonResult.Mismatch(ordered
            ? "the values match but the rows are not paired the same way or not in the reference order"
            : "each column has the right values but the rows do not pair up the same way");
    }

    private static bool CanAssignDistinct(List<int>[] candidates, int actualColumnCount)
    {
        var owner = new int[actualColumnCount];
        Array.Fill(owner, -1);

        bool Place(int column, bool[] seen)
        {
            foreach (var candidate in candidates[column])
            {
                if (seen[candidate]) continue;
                seen[candidate] = true;
                if (owner[candidate] < 0 || Place(owner[candidate], seen))
                {
                    owner[candidate] = column;
                    return true;
                }
            }

            return false;
        }

        return Enumerable.Range(0, candidates.Length).All(i => Place(i, new bool[actualColumnCount]));
    }

    private static bool RowsAgree(Cell[][] expected, Cell[][] actual, int[] assignment, int rowCount, bool ordered)
    {
        var width = expected.Length;
        Cell[] ExpectedRow(int r) => Enumerable.Range(0, width).Select(c => expected[c][r]).ToArray();
        Cell[] ActualRow(int r) => Enumerable.Range(0, width).Select(c => actual[assignment[c]][r]).ToArray();

        var expectedRows = Enumerable.Range(0, rowCount).Select(ExpectedRow).ToList();
        var actualRows = Enumerable.Range(0, rowCount).Select(ActualRow).ToList();
        if (!ordered)
        {
            expectedRows.Sort(CompareRows);
            actualRows.Sort(CompareRows);
        }

        for (var r = 0; r < rowCount; r++)
        {
            for (var c = 0; c < width; c++)
            {
                if (!Equal(expectedRows[r][c], actualRows[r][c])) return false;
            }
        }

        return true;
    }

    private static Cell[][] Columnize(QueryResult result)
    {
        var columns = new Cell[result.Columns.Count][];
        for (var c = 0; c < columns.Length; c++)
        {
            columns[c] = new Cell[result.RowCount];
            for (var r = 0; r < result.RowCount; r++) columns[c][r] = Normalize(result.Rows[r][c]);
        }

        return columns;
    }

    private static Cell[] SortedCopy(Cell[] column)
    {
        var copy = (Cell[])column.Clone();
        Array.Sort(copy, CompareCells);
        return copy;
    }

    private static bool SameValues(Cell[] sortedA, Cell[] sortedB)
    {
        for (var i = 0; i < sortedA.Length; i++)
        {
            if (!Equal(sortedA[i], sortedB[i])) return false;
        }

        return true;
    }

    private enum Kind { Null, Number, Text, Moment, Time }

    private readonly record struct Cell(Kind Kind, double Number = 0, string? Text = null, long Ticks = 0);

    private static Cell Normalize(object? value) => value switch
    {
        null or DBNull => new Cell(Kind.Null),
        bool b => new Cell(Kind.Number, b ? 1 : 0),
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double =>
            new Cell(Kind.Number, Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        decimal d => new Cell(Kind.Number, (double)d),
        string s => new Cell(Kind.Text, Text: s.TrimEnd()),
        char ch => new Cell(Kind.Text, Text: ch.ToString()),
        DateTime dt => new Cell(Kind.Moment, Ticks: dt.Ticks),
        DateTimeOffset dto => new Cell(Kind.Moment, Ticks: dto.UtcDateTime.Ticks),
        DateOnly date => new Cell(Kind.Moment, Ticks: date.ToDateTime(TimeOnly.MinValue).Ticks),
        TimeSpan time => new Cell(Kind.Time, Ticks: time.Ticks),
        TimeOnly time => new Cell(Kind.Time, Ticks: time.Ticks),
        Guid guid => new Cell(Kind.Text, Text: guid.ToString("D")),
        byte[] bytes => new Cell(Kind.Text, Text: Convert.ToHexString(bytes)),
        _ => new Cell(Kind.Text, Text: Convert.ToString(value, CultureInfo.InvariantCulture)?.TrimEnd()),
    };

    private static bool Equal(Cell a, Cell b) => a.Kind == b.Kind && a.Kind switch
    {
        Kind.Null => true,
        Kind.Number => (double.IsNaN(a.Number) && double.IsNaN(b.Number)) || Math.Abs(a.Number - b.Number) <= NumericTolerance,
        Kind.Text => string.Equals(a.Text, b.Text, StringComparison.Ordinal),
        _ => a.Ticks == b.Ticks,
    };

    /// <summary>
    /// A total order for sorting. Numbers are ordered by their value rounded to the tolerance, so values that only
    /// differ by noise sort next to each other and then pass <see cref="Equal"/>.
    /// </summary>
    private static int CompareCells(Cell a, Cell b)
    {
        if (a.Kind != b.Kind) return a.Kind.CompareTo(b.Kind);
        return a.Kind switch
        {
            Kind.Null => 0,
            Kind.Number => Math.Round(a.Number, 6).CompareTo(Math.Round(b.Number, 6)),
            Kind.Text => string.CompareOrdinal(a.Text, b.Text),
            _ => a.Ticks.CompareTo(b.Ticks),
        };
    }

    private static int CompareRows(Cell[] a, Cell[] b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            var order = CompareCells(a[i], b[i]);
            if (order != 0) return order;
        }

        return 0;
    }
}
