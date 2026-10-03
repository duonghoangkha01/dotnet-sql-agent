namespace SqlAgent.Core.Tests;

public class QueryLimitsTests
{
    [Fact]
    public void Defaults_are_500_rows_2mb_and_15_seconds()
    {
        var limits = new QueryLimits();

        Assert.Equal(500, limits.MaxRows);
        Assert.Equal(2 * 1024 * 1024, limits.MaxResultBytes);
        Assert.Equal(TimeSpan.FromSeconds(15), limits.CommandTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_max_rows_is_rejected(int maxRows)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryLimits(MaxRows: maxRows));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_max_result_bytes_is_rejected(long maxResultBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryLimits(MaxResultBytes: maxResultBytes));
    }
}
