using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Tax;

namespace TallyNext.UnitTests.Domain;

public sealed class GstTests
{
    private static GstRateEntry Rate(decimal igstPercent, decimal cessPercent = 0, Taxability taxability = Taxability.Taxable) =>
        new(new DateOnly(2026, 4, 1), taxability, GstRateEntry.ToMilliPercent(igstPercent, "igst_rate"), GstRateEntry.ToMilliPercent(cessPercent, "cess_rate"), "8471");

    [Theory]
    [InlineData("27", "27", GstRegistrationType.Regular, SupplyNature.IntraState)]
    [InlineData("27", null, GstRegistrationType.Unregistered, SupplyNature.IntraState)]
    [InlineData("27", "29", GstRegistrationType.Regular, SupplyNature.InterState)]
    [InlineData("27", "29", GstRegistrationType.Consumer, SupplyNature.InterState)]
    [InlineData("04", "04", GstRegistrationType.Regular, SupplyNature.IntraStateUnionTerritory)] // Chandigarh
    [InlineData("38", "38", GstRegistrationType.Unregistered, SupplyNature.IntraStateUnionTerritory)] // Ladakh
    [InlineData("07", "07", GstRegistrationType.Regular, SupplyNature.IntraState)] // Delhi has a legislature
    [InlineData("27", "27", GstRegistrationType.SpecialEconomicZone, SupplyNature.InterState)]
    [InlineData("27", "27", GstRegistrationType.Overseas, SupplyNature.InterState)]
    public void Place_of_supply_determines_the_supply_type(string supplier, string? placeOfSupply, GstRegistrationType party, SupplyNature expected)
    {
        Assert.Equal(expected, PlaceOfSupply.Determine(supplier, placeOfSupply, party));
    }

    [Fact]
    public void Place_of_supply_needs_the_supplier_state()
    {
        var ex = Assert.Throws<DomainException>(() => PlaceOfSupply.Determine(null, "27", GstRegistrationType.Regular));
        Assert.Equal("gst.state_missing", ex.Code);
    }

    [Fact]
    public void Intra_state_tax_splits_into_equal_central_and_state_halves()
    {
        var tax = GstCalculator.Compute(Money.FromDecimal(1_000m), Rate(18), SupplyNature.IntraState);
        Assert.Equal(90m, tax.Cgst.ToDecimal());
        Assert.Equal(90m, tax.StateTax.ToDecimal());
        Assert.Equal(0m, tax.Igst.ToDecimal());
        Assert.Equal(180m, tax.Total.ToDecimal());
    }

    [Fact]
    public void Inter_state_tax_is_integrated_and_cess_is_added_on_top()
    {
        var tax = GstCalculator.Compute(Money.FromDecimal(1_000m), Rate(28, 12), SupplyNature.InterState);
        Assert.Equal(0m, tax.Cgst.ToDecimal());
        Assert.Equal(280m, tax.Igst.ToDecimal());
        Assert.Equal(120m, tax.Cess.ToDecimal());
    }

    [Theory]
    // 1.00 at 5%: each half is 0.025 -> 0.03 (half away from zero), so CGST+SGST = 0.06 while IGST = 0.05.
    [InlineData("1.00", "5", "0.03", "0.05")]
    // 100.05 at 18%: halves 9.0045 -> 9.00 each (18.00) while IGST 18.009 -> 18.01.
    [InlineData("100.05", "18", "9.00", "18.01")]
    // 0.10 at 0.25%: 0.000125 rounds to zero on both bases.
    [InlineData("0.10", "0.25", "0.00", "0.00")]
    [InlineData("333.33", "12", "20.00", "40.00")]
    public void Each_head_is_rounded_to_the_paisa_half_away_from_zero(string taxable, string percent, string expectedHalf, string expectedIgst)
    {
        var value = Money.FromDecimal(decimal.Parse(taxable, System.Globalization.CultureInfo.InvariantCulture));
        var rate = Rate(decimal.Parse(percent, System.Globalization.CultureInfo.InvariantCulture));

        var intra = GstCalculator.Compute(value, rate, SupplyNature.IntraState);
        var inter = GstCalculator.Compute(value, rate, SupplyNature.InterState);

        var half = decimal.Parse(expectedHalf, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(half, intra.Cgst.ToDecimal());
        Assert.Equal(half, intra.StateTax.ToDecimal());
        Assert.Equal(decimal.Parse(expectedIgst, System.Globalization.CultureInfo.InvariantCulture), inter.Igst.ToDecimal());
    }

    [Theory]
    [InlineData(Taxability.Exempt)]
    [InlineData(Taxability.NilRated)]
    [InlineData(Taxability.NonGst)]
    public void Untaxed_supplies_carry_no_tax(Taxability taxability)
    {
        var tax = GstCalculator.Compute(Money.FromDecimal(500m), Rate(0, 0, taxability), SupplyNature.IntraState);
        Assert.Equal(Money.Zero, tax.Total);
    }

    [Fact]
    public void Union_territory_supplies_use_the_state_tax_slot_for_utgst()
    {
        var tax = GstCalculator.Compute(Money.FromDecimal(200m), Rate(12), SupplyNature.IntraStateUnionTerritory);
        Assert.Equal(12m, tax.Cgst.ToDecimal());
        Assert.Equal(12m, tax.StateTax.ToDecimal());
    }

    [Fact]
    public void Rate_schedule_resolves_the_rate_in_force_on_a_date()
    {
        var schedule = GstRateSchedule.Normalize(
        [
            new GstRateEntry(new DateOnly(2026, 10, 1), Taxability.Taxable, 18_000, 0, "8471"),
            new GstRateEntry(new DateOnly(2026, 4, 1), Taxability.Taxable, 12_000, 0, "8471"),
        ]);

        Assert.Null(GstRateSchedule.Resolve(schedule, new DateOnly(2026, 3, 31)));
        Assert.Equal(12_000, GstRateSchedule.Resolve(schedule, new DateOnly(2026, 9, 30))!.IgstMilliPercent);
        Assert.Equal(18_000, GstRateSchedule.Resolve(schedule, new DateOnly(2026, 10, 1))!.IgstMilliPercent);
        Assert.Equal(18_000, GstRateSchedule.Resolve(schedule, new DateOnly(2030, 1, 1))!.IgstMilliPercent);
    }

    [Fact]
    public void Rate_entries_are_validated()
    {
        Assert.Equal("gst.rate_for_untaxed", Assert.Throws<DomainException>(() => new GstRateEntry(new DateOnly(2026, 4, 1), Taxability.Exempt, 5_000, 0, null).Validate()).Code);
        Assert.Equal("gst.rate_invalid", Assert.Throws<DomainException>(() => new GstRateEntry(new DateOnly(2026, 4, 1), Taxability.Taxable, 100_001, 0, null).Validate()).Code);
        Assert.Equal("gst.hsn_invalid", Assert.Throws<DomainException>(() => new GstRateEntry(new DateOnly(2026, 4, 1), Taxability.Taxable, 5_000, 0, "12AB").Validate()).Code);
        Assert.Equal("gst.igst_rate_invalid", Assert.Throws<DomainException>(() => GstRateEntry.ToMilliPercent(12.0001m, "igst_rate")).Code);
    }
}
