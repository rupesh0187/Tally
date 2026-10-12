using TallyNext.Domain.Common;

namespace TallyNext.UnitTests.Domain;

public sealed class MoneyTests
{
    [Theory]
    [InlineData("0", 0L)]
    [InlineData("0.01", 1L)]
    [InlineData("12.34", 1234L)]
    [InlineData("12.3", 1230L)]
    [InlineData("-99.99", -9999L)]
    [InlineData("10000000000000", 1_000_000_000_000_000L)]
    public void FromDecimal_converts_exactly(string input, long expectedMinor)
    {
        var money = Money.FromDecimal(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(expectedMinor, money.Minor);
    }

    [Theory]
    [InlineData("0.001")]
    [InlineData("12.345")]
    [InlineData("1.999")]
    public void FromDecimal_rejects_sub_paise_precision_instead_of_rounding(string input)
    {
        var ex = Assert.Throws<DomainException>(() => Money.FromDecimal(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal("money.precision", ex.Code);
    }

    [Fact]
    public void FromDecimal_rejects_amounts_beyond_the_supported_range()
    {
        Assert.Equal("money.out_of_range", Assert.Throws<DomainException>(() => Money.FromDecimal(10_000_000_000_000.01m)).Code);
        Assert.Equal("money.out_of_range", Assert.Throws<DomainException>(() => Money.FromDecimal(decimal.MaxValue)).Code);
    }

    [Theory]
    [InlineData("0.005", "0.01")]
    [InlineData("0.015", "0.02")]
    [InlineData("-0.005", "-0.01")]
    [InlineData("2.675", "2.68")]
    [InlineData("1.004", "1.00")]
    public void Round_uses_half_away_from_zero(string input, string expected)
    {
        var rounded = Money.Round(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), rounded.ToDecimal());
    }

    [Fact]
    public void Round_supports_bankers_rounding_when_requested()
    {
        Assert.Equal(0.02m, Money.Round(0.025m, MidpointRounding.ToEven).ToDecimal());
    }

    [Fact]
    public void Arithmetic_is_exact_and_round_trips()
    {
        var a = Money.FromDecimal(0.10m);
        var b = Money.FromDecimal(0.20m);
        Assert.Equal(Money.FromDecimal(0.30m), a + b);
        Assert.Equal(0.30m, (a + b).ToDecimal());
        Assert.Equal("0.30", (a + b).ToString());
        Assert.Equal(-10L, (a - b).Minor);
    }

    [Fact]
    public void Sum_detects_overflow()
    {
        var huge = Money.FromMinor(Money.MaxAbsMinor);
        var many = Enumerable.Repeat(huge, 10_000);
        Assert.Throws<OverflowException>(() => Money.Sum(many));
    }
}
