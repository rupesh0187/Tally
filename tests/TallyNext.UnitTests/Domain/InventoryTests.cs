using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Inventory;
using TallyNext.Domain.Tax;
using TallyNext.Domain.Vouchers;

namespace TallyNext.UnitTests.Domain;

public sealed class StockValuationTests
{
    private static readonly DateOnly From = new(2026, 4, 1);
    private static readonly DateOnly To = new(2027, 3, 31);

    private static long Q(decimal quantity) => StockMath.ToQuantityMilli(quantity, 3);

    private static long V(decimal value) => Money.FromDecimal(value).Minor;

    private static StockMovement Purchase(int day, decimal quantity, decimal value) => new(From.AddDays(day), StockDirection.In, Q(quantity), V(value));

    private static StockMovement Sale(int day, decimal quantity) => new(From.AddDays(day), StockDirection.Out, Q(quantity), null);

    private static StockMovement SalesReturn(int day, decimal quantity) => new(From.AddDays(day), StockDirection.In, Q(quantity), null);

    [Fact]
    public void Weighted_average_costs_each_sale_at_the_running_average()
    {
        var result = StockValuation.Run(
            ValuationMethod.WeightedAverage,
            new StockPosition(Q(10), V(1_000m)),
            [Purchase(5, 10, 1_200m), Sale(6, 5)],
            From, To);

        Assert.Equal(V(550m), result.OutwardCostMinor); // average 110
        Assert.Equal(Q(15), result.Closing.QuantityMilli);
        Assert.Equal(V(1_650m), result.Closing.ValueMinor);
        Assert.Equal(V(1_200m), result.InwardValueMinor);
        Assert.False(result.WentNegative);
    }

    [Fact]
    public void Fifo_consumes_the_oldest_lots_first()
    {
        var result = StockValuation.Run(
            ValuationMethod.Fifo,
            new StockPosition(Q(10), V(1_000m)),
            [Purchase(5, 10, 1_200m), Sale(6, 15)],
            From, To);

        Assert.Equal(V(1_000m + 600m), result.OutwardCostMinor);
        Assert.Equal(Q(5), result.Closing.QuantityMilli);
        Assert.Equal(V(600m), result.Closing.ValueMinor);
    }

    [Fact]
    public void Opening_for_a_period_includes_earlier_movements()
    {
        var result = StockValuation.Run(
            ValuationMethod.WeightedAverage,
            new StockPosition(0, 0),
            [Purchase(0, 4, 400m), Sale(1, 1), Purchase(40, 2, 260m)],
            From.AddDays(30), To);

        Assert.Equal(new StockPosition(Q(3), V(300m)), result.Opening);
        Assert.Equal(Q(2), result.InwardQuantityMilli);
        Assert.Equal(0, result.OutwardQuantityMilli);
        Assert.Equal(new StockPosition(Q(5), V(560m)), result.Closing);
    }

    [Fact]
    public void Sales_returns_come_back_at_the_current_cost()
    {
        var result = StockValuation.Run(
            ValuationMethod.WeightedAverage,
            new StockPosition(Q(10), V(1_000m)),
            [Sale(1, 4), SalesReturn(2, 1)],
            From, To);

        Assert.Equal(new StockPosition(Q(7), V(700m)), result.Closing);
    }

    [Theory]
    [InlineData(ValuationMethod.WeightedAverage)]
    [InlineData(ValuationMethod.Fifo)]
    public void Negative_stock_is_allowed_and_valued_at_the_last_known_cost(ValuationMethod method)
    {
        // Two offline devices each sold the last units; the books must still produce a value.
        var result = StockValuation.Run(method, new StockPosition(0, 0), [Purchase(0, 5, 50m), Sale(1, 7)], From, To);

        Assert.True(result.WentNegative);
        Assert.Equal(V(70m), result.OutwardCostMinor);
        Assert.Equal(new StockPosition(Q(-2), V(-20m)), result.Closing);
    }

    [Fact]
    public void Fifo_shortfall_is_filled_first_by_the_next_purchase()
    {
        var result = StockValuation.Run(ValuationMethod.Fifo, new StockPosition(0, 0), [Purchase(0, 5, 50m), Sale(1, 7), Purchase(2, 4, 48m)], From, To);

        Assert.Equal(new StockPosition(Q(2), V(24m)), result.Closing);
    }

    [Fact]
    public void Movements_after_the_period_are_ignored()
    {
        var result = StockValuation.Run(ValuationMethod.Fifo, new StockPosition(Q(1), V(10m)), [Sale(400, 1)], From, To);
        Assert.Equal(new StockPosition(Q(1), V(10m)), result.Closing);
    }

    [Fact]
    public void Line_value_is_quantity_times_rate_less_discount_rounded_to_the_paisa()
    {
        // 2.5 kg at 99.99 less 10% = 224.9775 -> 224.98
        var value = StockMath.LineValue(StockMath.ToQuantityMilli(2.5m, 3), StockMath.ToRateScaled(99.99m), GstRateEntry.ToMilliPercent(10m, "discount"));
        Assert.Equal(224.98m, value.ToDecimal());
    }

    [Fact]
    public void Quantities_respect_the_unit_decimal_places()
    {
        Assert.Equal(3_000, StockMath.ToQuantityMilli(3m, 0));
        Assert.Throws<DomainException>(() => StockMath.ToQuantityMilli(1.5m, 0));
        Assert.Equal(1_250, StockMath.ToQuantityMilli(1.25m, 2));
    }
}

public sealed class InvoiceBuilderTests
{
    private static readonly Guid Party = Guid.NewGuid();
    private static readonly Guid SalesLedger = Guid.NewGuid();
    private static readonly Guid ServiceLedger = Guid.NewGuid();
    private static readonly Guid Item = Guid.NewGuid();
    private static readonly Guid Godown = Guid.NewGuid();
    private static readonly Guid RoundOff = Guid.NewGuid();
    private static readonly InvoiceTaxLedgers Taxes = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static readonly GstRateEntry Gst18 = new(new DateOnly(2026, 4, 1), Taxability.Taxable, 18_000, 0, "8471");

    private static InvoiceLineInput StockLine(decimal quantity, decimal rate, Guid? ledger = null) =>
        new(null, InvoiceLineKind.Stock, Item, Godown, ledger ?? SalesLedger, null, StockMath.ToQuantityMilli(quantity, 0), StockMath.ToRateScaled(rate), 0, null, Gst18);

    private static long Sum(InvoiceDraft draft, DrCr side) => draft.AccountingLines.Where(l => l.Side == side).Sum(l => l.Amount.Minor);

    [Fact]
    public void Sales_invoice_debits_the_party_and_credits_sales_and_output_tax()
    {
        var draft = InvoiceBuilder.Build(VoucherBaseType.Sales, Party, SupplyNature.IntraState, chargeTax: true,
            [StockLine(3, 150m), StockLine(2, 100m)], Taxes, null);

        Assert.Equal(650m, draft.Taxable.ToDecimal());
        Assert.Equal(58.50m, draft.Cgst.ToDecimal());
        Assert.Equal(58.50m, draft.StateTax.ToDecimal());
        Assert.Equal(767m, draft.GrandTotal.ToDecimal());
        Assert.Equal(Sum(draft, DrCr.Debit), Sum(draft, DrCr.Credit));

        var party = draft.AccountingLines[0];
        Assert.Equal((Party, DrCr.Debit, 767m), (party.LedgerId, party.Side, party.Amount.ToDecimal()));
        var sales = Assert.Single(draft.AccountingLines, l => l.LedgerId == SalesLedger);
        Assert.Equal((DrCr.Credit, 650m), (sales.Side, sales.Amount.ToDecimal())); // aggregated per ledger
        Assert.Contains(draft.AccountingLines, l => l.LedgerId == Taxes.Cgst && l.Side == DrCr.Credit && l.Amount.ToDecimal() == 58.50m);
        Assert.Contains(draft.AccountingLines, l => l.LedgerId == Taxes.Sgst && l.Side == DrCr.Credit && l.Amount.ToDecimal() == 58.50m);
        Assert.All(draft.ItemLines, l => Assert.Equal(StockDirection.Out, l.Direction));
    }

    [Fact]
    public void Purchase_invoice_credits_the_party_and_debits_input_tax()
    {
        var draft = InvoiceBuilder.Build(VoucherBaseType.Purchase, Party, SupplyNature.InterState, chargeTax: true,
            [StockLine(10, 120m)], Taxes, null);

        Assert.Equal((Party, DrCr.Credit, 1_416m), (draft.AccountingLines[0].LedgerId, draft.AccountingLines[0].Side, draft.AccountingLines[0].Amount.ToDecimal()));
        Assert.Contains(draft.AccountingLines, l => l.LedgerId == Taxes.Igst && l.Side == DrCr.Debit && l.Amount.ToDecimal() == 216m);
        Assert.All(draft.ItemLines, l => Assert.Equal(StockDirection.In, l.Direction));
    }

    [Fact]
    public void Union_territory_supplies_post_to_utgst_not_sgst()
    {
        var draft = InvoiceBuilder.Build(VoucherBaseType.Sales, Party, SupplyNature.IntraStateUnionTerritory, chargeTax: true, [StockLine(1, 100m)], Taxes, null);
        Assert.Contains(draft.AccountingLines, l => l.LedgerId == Taxes.Utgst);
        Assert.DoesNotContain(draft.AccountingLines, l => l.LedgerId == Taxes.Sgst);
    }

    [Fact]
    public void Round_off_brings_the_total_to_whole_rupees_and_stays_balanced()
    {
        // 1 x 99.99 + 18% (8.9991 -> 9.00 each half) = 117.99 -> 118.00 with +0.01 round off.
        var draft = InvoiceBuilder.Build(VoucherBaseType.Sales, Party, SupplyNature.IntraState, chargeTax: true, [StockLine(1, 99.99m)], Taxes, RoundOff);

        Assert.Equal(0.01m, draft.RoundOff.ToDecimal());
        Assert.Equal(118m, draft.GrandTotal.ToDecimal());
        Assert.Equal(Sum(draft, DrCr.Debit), Sum(draft, DrCr.Credit));
        Assert.Contains(draft.AccountingLines, l => l.LedgerId == RoundOff);
    }

    [Fact]
    public void Service_lines_use_their_amount_and_never_move_stock()
    {
        var service = new InvoiceLineInput(null, InvoiceLineKind.Service, null, null, ServiceLedger, "Installation", 0, 0, 0, Money.FromDecimal(500m), Gst18);
        var draft = InvoiceBuilder.Build(VoucherBaseType.Sales, Party, SupplyNature.IntraState, chargeTax: true, [StockLine(1, 100m), service], Taxes, null);

        var line = draft.ItemLines.Single(l => l.Kind == InvoiceLineKind.Service);
        Assert.Equal(StockDirection.None, line.Direction);
        Assert.Null(line.StockItemId);
        Assert.Equal(708m, draft.GrandTotal.ToDecimal());
    }

    [Fact]
    public void Without_tax_no_tax_lines_are_posted()
    {
        var draft = InvoiceBuilder.Build(VoucherBaseType.Sales, Party, SupplyNature.IntraState, chargeTax: false, [StockLine(2, 50m)], new InvoiceTaxLedgers(null, null, null, null, null), null);
        Assert.Equal(2, draft.AccountingLines.Count);
        Assert.Equal(100m, draft.GrandTotal.ToDecimal());
    }

    [Fact]
    public void A_missing_tax_ledger_is_reported()
    {
        var ex = Assert.Throws<DomainException>(() =>
            InvoiceBuilder.Build(VoucherBaseType.Sales, Party, SupplyNature.InterState, chargeTax: true, [StockLine(1, 10m)], new InvoiceTaxLedgers(null, null, null, null, null), null));
        Assert.Equal("gst.tax_ledger_missing", ex.Code);
    }

    [Theory]
    [InlineData(VoucherBaseType.CreditNote, StockDirection.In, DrCr.Credit)]
    [InlineData(VoucherBaseType.DebitNote, StockDirection.Out, DrCr.Debit)]
    public void Notes_move_stock_and_the_party_the_right_way(VoucherBaseType type, StockDirection stock, DrCr partySide)
    {
        var draft = InvoiceBuilder.Build(type, Party, SupplyNature.IntraState, chargeTax: true, [StockLine(1, 100m)], Taxes, null);
        Assert.Equal(stock, draft.ItemLines[0].Direction);
        Assert.Equal(partySide, draft.AccountingLines[0].Side);
    }

    [Fact]
    public void Journal_vouchers_cannot_carry_invoice_lines()
    {
        Assert.Throws<DomainException>(() => InvoiceBuilder.Build(VoucherBaseType.Journal, Party, SupplyNature.IntraState, true, [StockLine(1, 1m)], Taxes, null));
    }
}
