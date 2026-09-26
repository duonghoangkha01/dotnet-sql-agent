using SqlAgent.Core.Schema;

namespace SqlAgent.Core.Tests.Schema;

public class NameNormalizerAndGlobTests
{
    [Theory]
    [InlineData("Sales.SalesOrderHeader", "Sales.SalesOrderHeader")]
    [InlineData("[Sales].[SalesOrderHeader]", "Sales.SalesOrderHeader")]
    [InlineData("\"Sales\".\"SalesOrderHeader\"", "Sales.SalesOrderHeader")]
    [InlineData("  [Sales] . SalesOrderHeader ", "Sales.SalesOrderHeader")]
    [InlineData("[Sales].[Order.Header]", "Sales.Order.Header")]
    public void Normalize_strips_brackets_quotes_and_whitespace(string raw, string expected) =>
        Assert.Equal(expected, NameNormalizer.Normalize(raw));

    [Fact]
    public void A_dot_inside_brackets_stays_in_one_part() =>
        Assert.Equal(new[] { "Sales", "Order.Header" }, NameNormalizer.SplitParts("[Sales].[Order.Header]"));

    [Fact]
    public void Normalized_names_compare_case_insensitively_in_sets()
    {
        var set = new HashSet<string>([NameNormalizer.Table("Sales", "SalesOrderHeader")], StringComparer.OrdinalIgnoreCase);
        Assert.Contains(NameNormalizer.Normalize("[SALES].[salesorderheader]"), set);
    }

    [Theory]
    [InlineData("Password*", "PasswordHash", true)]
    [InlineData("password*", "PasswordSalt", true)]
    [InlineData("Password*", "Pass", false)]
    [InlineData("*Number", "CardNumber", true)]
    [InlineData("Rate?", "Rate1", true)]
    [InlineData("Rate?", "Rate12", false)]
    [InlineData("Name", "Name", true)]
    [InlineData("a.b", "aXb", false)]
    public void Glob_matches_case_insensitively(string pattern, string name, bool expected) =>
        Assert.Equal(expected, new GlobPattern(pattern).IsMatch(name));

    [Theory]
    [InlineData("Password*", "Password%")]
    [InlineData("Rate?", "Rate_")]
    [InlineData("Sales_Total", "Sales\\_Total")]
    [InlineData("100%", "100\\%")]
    public void Glob_converts_to_an_escaped_like_pattern(string pattern, string expected) =>
        Assert.Equal(expected, GlobPattern.ToLikePattern(pattern));
}
