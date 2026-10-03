using SqlAgent.Core.Agent;
using SqlAgent.Core.Execution;

namespace SqlAgent.Core.Tests.Agent;

public class NumberGroundingTests
{
    private static QueryResult Result(params object?[][] rows) =>
        new([new QueryColumn("a", "int"), new QueryColumn("b", "int")], rows, Truncated: false, ElapsedMs: 1);

    private static IReadOnlyList<string> Ungrounded(string answer, QueryResult result, string question = "") =>
        NumberGrounding.UngroundedFigures(answer, question, [result]);

    [Theory]
    [InlineData("There were 1234 orders.")]
    [InlineData("There were 1,234 orders.")]
    [InlineData("There were 1.234 orders.")]
    public void Formatting_of_thousands_does_not_matter(string answer)
    {
        Assert.Empty(Ungrounded(answer, Result([1234, "x"])));
    }

    [Theory]
    [InlineData("Total: 1,234.56", 1234.56)]
    [InlineData("Total: 1.234,56", 1234.56)]
    [InlineData("Total: 1234.56", 1234.56)]
    [InlineData("Total: 1234.6", 1234.56)]
    [InlineData("Total: 1,235", 1234.56)]
    public void Decimals_match_in_either_convention_and_a_rounding_is_accepted(string answer, double value)
    {
        Assert.Empty(Ungrounded(answer, Result([(decimal)value, "x"])));
    }

    [Fact]
    public void A_figure_that_is_in_no_row_is_reported()
    {
        var ungrounded = Ungrounded("Sales were 9,999 in total.", Result([1234, "x"]));

        Assert.Equal(["9,999"], ungrounded);
    }

    [Fact]
    public void A_rounding_must_be_to_the_precision_that_is_written()
    {
        Assert.NotEmpty(Ungrounded("Total: 1234.9", Result([1234.56m, "x"])));
        Assert.NotEmpty(Ungrounded("Total: 1240", Result([1234.56m, "x"])));
    }

    [Theory]
    [InlineData("About 1.2 million units.")]
    [InlineData("About 1.2M units.")]
    [InlineData("About 1,2 triệu đơn vị.")]
    [InlineData("About 1235 thousand units.")]
    public void Scale_words_are_understood(string answer)
    {
        Assert.Empty(Ungrounded(answer, Result([1_234_567, "x"])));
    }

    [Fact]
    public void A_percentage_matches_the_value_or_its_fraction()
    {
        Assert.Empty(Ungrounded("That is 12.5%.", Result([0.125m, "x"])));
        Assert.Empty(Ungrounded("That is 12.5%.", Result([12.5m, "x"])));
    }

    [Fact]
    public void Row_counts_dates_and_numbers_inside_text_cells_are_grounded()
    {
        var result = Result([1, "Road-150 Red, 62"], [2, "x"]);

        Assert.Empty(Ungrounded("2 rows. The bike size is 62.", result));
        Assert.Empty(Ungrounded("Ordered in 2013.", Result([new DateTime(2013, 5, 31), "x"])));
    }

    [Fact]
    public void Numbers_the_user_wrote_may_be_repeated()
    {
        Assert.Empty(Ungrounded("Here are the top 5 products.", Result([1, "x"]), question: "Show me the top 5 products"));
    }

    [Fact]
    public void List_numbering_and_codes_glued_to_letters_are_not_figures()
    {
        const string answer = "1. First place\n2) Second place\nFor Q4 and AW2022 see below.";

        Assert.Empty(Ungrounded(answer, Result([7, "x"])));
    }

    [Fact]
    public void Negative_values_match_by_magnitude_and_nulls_are_ignored()
    {
        Assert.Empty(Ungrounded("A loss of 500.", Result([-500, null])));
    }

    [Fact]
    public void A_figure_the_model_computed_itself_is_not_found()
    {
        // 40 + 2 are both in the rows; their sum is not.
        Assert.Equal(["42"], Ungrounded("Together they sold 42.", Result([40, 2])));
    }

    [Fact]
    public void ContainsFigures_and_FiguresNotInQuestion_support_the_no_data_check()
    {
        Assert.False(NumberGrounding.ContainsFigures("Which year do you mean?"));
        Assert.True(NumberGrounding.ContainsFigures("There are 12 tables."));
        Assert.Empty(NumberGrounding.FiguresNotInQuestion("You asked about 2013.", "orders in 2013"));
        Assert.Equal(["12"], NumberGrounding.FiguresNotInQuestion("There are 12 tables.", "how many tables"));
    }

    [Theory]
    [InlineData("It was 999999999999999999 billion.")]
    [InlineData("It was 99999999999999999 billion, or 99999999999999999%.")]
    public void Absurdly_large_figures_are_reported_not_thrown(string answer)
    {
        // Neither the question nor the data hold them; the point is that checking them does not overflow.
        Assert.NotEmpty(Ungrounded(answer, Result([decimal.MaxValue, "x"])));
        Assert.Empty(Ungrounded(answer, Result([1, "x"]), question: "I asked about " + answer));
        Assert.NotEmpty(Ungrounded(answer, Result([1, "x"])));
    }
}
