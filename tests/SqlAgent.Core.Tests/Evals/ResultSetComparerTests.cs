using SqlAgent.Core.Evals;
using SqlAgent.Core.Execution;

namespace SqlAgent.Core.Tests.Evals;

public class ResultSetComparerTests
{
    private static QueryResult Result(string[] columns, params object?[][] rows) => new(
        columns.Select(c => new QueryColumn(c, "unknown")).ToList(), rows, Truncated: false, ElapsedMs: 1);

    private static readonly QueryResult Reference = Result(["Name", "Total"],
        ["Bikes", 100m], ["Clothing", 20.5m], ["Parts", 7m]);

    private static void AssertMatch(QueryResult reference, QueryResult actual, bool ordered = false)
    {
        var result = ResultSetComparer.Compare(reference, actual, ordered);
        Assert.True(result.Match, result.Reason);
    }

    private static string AssertMismatch(QueryResult reference, QueryResult actual, bool ordered = false)
    {
        var result = ResultSetComparer.Compare(reference, actual, ordered);
        Assert.False(result.Match);
        return result.Reason;
    }

    [Fact]
    public void Identical_results_match()
    {
        AssertMatch(Reference, Reference);
    }

    [Fact]
    public void Column_names_do_not_matter_only_values()
    {
        AssertMatch(Reference, Result(["Category", "Revenue"], ["Bikes", 100m], ["Clothing", 20.5m], ["Parts", 7m]));
    }

    [Fact]
    public void Column_order_does_not_matter()
    {
        AssertMatch(Reference, Result(["Total", "Name"], [100m, "Bikes"], [20.5m, "Clothing"], [7m, "Parts"]));
    }

    [Fact]
    public void Extra_agent_columns_are_allowed_when_the_row_count_matches()
    {
        AssertMatch(Reference, Result(["Id", "Name", "Total", "Note"],
            [1, "Bikes", 100m, "x"], [2, "Clothing", 20.5m, "y"], [3, "Parts", 7m, "z"]));
    }

    [Fact]
    public void A_different_row_count_never_matches_even_with_matching_columns()
    {
        var reason = AssertMismatch(Reference, Result(["Name", "Total"], ["Bikes", 100m], ["Clothing", 20.5m]));
        Assert.Contains("row count", reason);
    }

    [Fact]
    public void A_missing_reference_column_is_a_mismatch_that_names_it()
    {
        var reason = AssertMismatch(Reference, Result(["Name", "Other"], ["Bikes", 1m], ["Clothing", 2m], ["Parts", 3m]));
        Assert.Contains("'Total'", reason);
    }

    [Fact]
    public void Row_order_is_ignored_unless_the_reference_is_ordered()
    {
        var shuffled = Result(["Name", "Total"], ["Parts", 7m], ["Bikes", 100m], ["Clothing", 20.5m]);

        AssertMatch(Reference, shuffled, ordered: false);
        AssertMismatch(Reference, shuffled, ordered: true);
        AssertMatch(Reference, Reference, ordered: true);
    }

    [Fact]
    public void An_ordered_match_works_across_permuted_columns()
    {
        AssertMatch(Reference, Result(["Total", "Name"], [100m, "Bikes"], [20.5m, "Clothing"], [7m, "Parts"]), ordered: true);
    }

    [Fact]
    public void Columns_with_the_right_values_but_wrong_pairing_do_not_match()
    {
        // Same names, same totals, but each name is next to another category's total.
        var reason = AssertMismatch(Reference, Result(["Name", "Total"], ["Bikes", 20.5m], ["Clothing", 7m], ["Parts", 100m]));
        Assert.Contains("pair", reason);
    }

    [Fact]
    public void Numbers_match_within_the_tolerance_and_across_numeric_types()
    {
        var reference = Result(["N"], [1], [2.5m], [3L]);

        AssertMatch(reference, Result(["X"], [1.0], [2.5000004], [3m]));
        AssertMismatch(reference, Result(["X"], [1.0], [2.5001], [3m]));
    }

    [Fact]
    public void Values_that_differ_only_by_float_noise_sort_and_match_in_unordered_results()
    {
        var reference = Result(["A", "B"], [0.1 + 0.2, "x"], [0.3000001, "y"]);

        AssertMatch(reference, Result(["A", "B"], [0.3000001, "y"], [0.30000000000000004, "x"]));
    }

    [Fact]
    public void A_number_does_not_match_text_that_looks_like_it()
    {
        AssertMismatch(Result(["N"], [5]), Result(["N"], ["5"]));
    }

    [Fact]
    public void Nulls_equal_nulls_and_nothing_else()
    {
        var reference = Result(["V"], [null], [1], [null]);

        AssertMatch(reference, Result(["V"], [null], [null], [1]));
        AssertMismatch(reference, Result(["V"], [0], [null], [1]));
        AssertMismatch(reference, Result(["V"], [DBNull.Value], [1], [2]), ordered: false);
    }

    [Fact]
    public void Dates_are_compared_by_value_whatever_their_type()
    {
        var reference = Result(["D"], [new DateTime(2013, 7, 1)], [new DateTime(2013, 7, 2)]);

        AssertMatch(reference, Result(["D"], [new DateOnly(2013, 7, 2)], [new DateOnly(2013, 7, 1)]));
        AssertMatch(reference, Result(["D"], [new DateTimeOffset(2013, 7, 1, 0, 0, 0, TimeSpan.Zero)], [new DateTime(2013, 7, 2)]));
        AssertMismatch(reference, Result(["D"], [new DateTime(2013, 7, 1, 12, 0, 0)], [new DateTime(2013, 7, 2)]));
    }

    [Fact]
    public void Trailing_spaces_are_ignored_but_case_is_not()
    {
        AssertMatch(Result(["N"], ["Bikes"]), Result(["N"], ["Bikes   "]));
        AssertMismatch(Result(["N"], ["Bikes"]), Result(["N"], ["bikes"]));
    }

    [Fact]
    public void Booleans_compare_as_zero_and_one()
    {
        AssertMatch(Result(["Flag"], [true], [false]), Result(["Flag"], [0], [1]));
    }

    [Fact]
    public void Two_reference_columns_with_identical_values_need_two_distinct_agent_columns()
    {
        var reference = Result(["A", "B"], [1, 1], [2, 2]);

        AssertMismatch(reference, Result(["X"], [1], [2]));
        AssertMatch(reference, Result(["X", "Y"], [1, 1], [2, 2]));
    }

    [Fact]
    public void Columns_that_share_values_are_assigned_so_the_rows_still_pair_up()
    {
        // Reference columns A and B hold the same values (1, 2) but pair differently with C.
        var reference = Result(["A", "B", "C"], [1, 2, "p"], [2, 1, "q"]);

        // The agent lists the same data with the first two columns swapped: greedy first-fit would pair them wrongly.
        AssertMatch(reference, Result(["X", "Y", "Z"], [2, 1, "p"], [1, 2, "q"]), ordered: true);
    }

    [Fact]
    public void Two_empty_results_match_when_the_agent_has_at_least_as_many_columns()
    {
        AssertMatch(Result(["A"]), Result(["A", "B"]));
        AssertMismatch(Result(["A"]), Result(["A"], [1]));
    }
}
