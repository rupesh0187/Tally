using TallyNext.Domain.Common;
using TallyNext.Domain.Inventory;
using TallyNext.Domain.Tax;

namespace TallyNext.Domain.Vouchers;

/// <param name="Amount">Value of a service line (when quantity/rate are not used). Ignored for stock lines.</param>
public sealed record InvoiceLineInput(
    Guid? Id,
    InvoiceLineKind Kind,
    Guid? StockItemId,
    Guid? GodownId,
    Guid LedgerId,
    string? Description,
    long QuantityMilli,
    long RateScaled,
    int DiscountMilliPercent,
    Money? Amount,
    GstRateEntry Rate);

/// <summary>Duty ledgers that receive each GST head (null where the company has none).</summary>
public sealed record InvoiceTaxLedgers(Guid? Cgst, Guid? Sgst, Guid? Utgst, Guid? Igst, Guid? Cess);

public sealed record InvoiceDraft(
    IReadOnlyList<VoucherLineSpec> AccountingLines,
    IReadOnlyList<VoucherItemLineSpec> ItemLines,
    Money Taxable,
    Money Cgst,
    Money StateTax,
    Money Igst,
    Money Cess,
    Money RoundOff,
    Money GrandTotal)
{
    public Money TotalTax => Cgst + StateTax + Igst + Cess;
}

/// <summary>
/// Turns invoice lines into a balanced double-entry voucher.
/// <list type="table">
/// <item><term>Sales</term><description>Dr party (grand total); Cr sales ledgers (taxable); Cr output tax ledgers; stock out.</description></item>
/// <item><term>Purchase</term><description>Cr party; Dr purchase ledgers; Dr input tax ledgers; stock in.</description></item>
/// <item><term>Credit note</term><description>(sales return) Cr party; Dr sales ledgers; Dr tax; stock in.</description></item>
/// <item><term>Debit note</term><description>(purchase return) Dr party; Cr purchase ledgers; Cr tax; stock out.</description></item>
/// </list>
/// Optional round-off to the nearest rupee is posted to the round-off ledger on whichever side balances the voucher.
/// The same function re-derives the postings on the server to verify uploaded invoices.
/// </summary>
public static class InvoiceBuilder
{
    public const int MaxLines = 300;

    public static bool IsInvoiceType(VoucherBaseType type) =>
        type is VoucherBaseType.Sales or VoucherBaseType.Purchase or VoucherBaseType.CreditNote or VoucherBaseType.DebitNote;

    /// <summary>The side on which goods/services and tax are posted.</summary>
    public static DrCr ItemSide(VoucherBaseType type) =>
        type is VoucherBaseType.Sales or VoucherBaseType.DebitNote ? DrCr.Credit : DrCr.Debit;

    public static StockDirection StockDirectionOf(VoucherBaseType type) =>
        type is VoucherBaseType.Purchase or VoucherBaseType.CreditNote ? StockDirection.In : StockDirection.Out;

    public static InvoiceDraft Build(
        VoucherBaseType type,
        Guid partyLedgerId,
        SupplyNature nature,
        bool chargeTax,
        IReadOnlyList<InvoiceLineInput> lines,
        InvoiceTaxLedgers taxLedgers,
        Guid? roundOffLedgerId)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(taxLedgers);
        if (!IsInvoiceType(type))
        {
            throw new DomainException("invoice.type_invalid", "Invoices are Sales, Purchase, Credit Note or Debit Note vouchers.");
        }

        if (partyLedgerId == Guid.Empty)
        {
            throw new DomainException("invoice.party_required", "Choose the party (customer or supplier).");
        }

        if (lines.Count == 0)
        {
            throw new DomainException("invoice.no_lines", "Add at least one item or service line.");
        }

        if (lines.Count > MaxLines)
        {
            throw new DomainException("invoice.too_many_lines", $"An invoice can have at most {MaxLines} lines.");
        }

        if (!Enum.IsDefined(nature))
        {
            throw new DomainException("invoice.nature_invalid", "The supply type is not valid.");
        }

        var direction = StockDirectionOf(type);
        var itemSpecs = new List<VoucherItemLineSpec>(lines.Count);
        var lineNo = 0;
        foreach (var line in lines)
        {
            lineNo++;
            if (line.LedgerId == Guid.Empty || line.LedgerId == partyLedgerId)
            {
                throw new DomainException("invoice.line_ledger_invalid", $"Line {lineNo}: choose the sales/purchase ledger.");
            }

            line.Rate.Validate();
            Money taxable;
            if (line.Kind == InvoiceLineKind.Stock)
            {
                if (line.StockItemId is null || line.GodownId is null)
                {
                    throw new DomainException("invoice.line_item_required", $"Line {lineNo}: choose the item and godown.");
                }

                if (line.QuantityMilli <= 0)
                {
                    throw new DomainException("invoice.line_quantity_invalid", $"Line {lineNo}: the quantity must be greater than zero.");
                }

                taxable = StockMath.LineValue(line.QuantityMilli, line.RateScaled, line.DiscountMilliPercent);
            }
            else
            {
                taxable = line.Amount ?? StockMath.LineValue(line.QuantityMilli, line.RateScaled, line.DiscountMilliPercent);
            }

            if (!taxable.IsPositive)
            {
                throw new DomainException("invoice.line_value_invalid", $"Line {lineNo}: the value must be greater than zero.");
            }

            var tax = chargeTax ? GstCalculator.Compute(taxable, line.Rate, nature) : default;
            itemSpecs.Add(new VoucherItemLineSpec(
                line.Id,
                line.Kind,
                line.Kind == InvoiceLineKind.Stock ? line.StockItemId : null,
                line.Kind == InvoiceLineKind.Stock ? line.GodownId : null,
                line.LedgerId,
                line.Description,
                line.Kind == InvoiceLineKind.Stock ? line.QuantityMilli : Math.Max(line.QuantityMilli, 0),
                line.RateScaled,
                line.DiscountMilliPercent,
                taxable,
                line.Kind == InvoiceLineKind.Stock ? direction : StockDirection.None,
                line.Rate,
                tax));
        }

        var taxableTotal = Money.Sum(itemSpecs.Select(s => s.Taxable));
        var cgst = Money.Sum(itemSpecs.Select(s => s.Tax.Cgst));
        var stateTax = Money.Sum(itemSpecs.Select(s => s.Tax.StateTax));
        var igst = Money.Sum(itemSpecs.Select(s => s.Tax.Igst));
        var cess = Money.Sum(itemSpecs.Select(s => s.Tax.Cess));
        var total = taxableTotal + cgst + stateTax + igst + cess;

        var itemSide = ItemSide(type);
        var partySide = itemSide.Opposite();
        var accounting = new List<VoucherLineSpec>();

        // Party first: registers and the Day Book show line 1 as the particulars.
        var roundOff = Money.Zero;
        if (roundOffLedgerId is { } roundOffLedger)
        {
            var rounded = Money.FromDecimal(Math.Round(total.ToDecimal(), 0, MidpointRounding.AwayFromZero));
            roundOff = rounded - total;
            if (!roundOff.IsZero && roundOffLedger == partyLedgerId)
            {
                throw new DomainException("invoice.round_off_ledger_invalid", "The round-off ledger cannot be the party.");
            }
        }

        var grandTotal = total + roundOff;
        accounting.Add(new VoucherLineSpec(partyLedgerId, partySide, grandTotal));

        foreach (var group in itemSpecs.GroupBy(s => s.LedgerId))
        {
            accounting.Add(new VoucherLineSpec(group.Key, itemSide, Money.Sum(group.Select(s => s.Taxable))));
        }

        void AddTax(Money amount, Guid? ledger, string head)
        {
            if (amount.IsZero)
            {
                return;
            }

            if (ledger is not { } id)
            {
                throw new DomainException("gst.tax_ledger_missing", $"Create a {head} ledger under Duties & Taxes (Company settings > GST).");
            }

            accounting.Add(new VoucherLineSpec(id, itemSide, amount));
        }

        AddTax(cgst, taxLedgers.Cgst, "CGST");
        AddTax(stateTax, nature == SupplyNature.IntraStateUnionTerritory ? taxLedgers.Utgst : taxLedgers.Sgst, nature == SupplyNature.IntraStateUnionTerritory ? "UTGST" : "SGST");
        AddTax(igst, taxLedgers.Igst, "IGST");
        AddTax(cess, taxLedgers.Cess, "Cess");

        if (!roundOff.IsZero)
        {
            accounting.Add(new VoucherLineSpec(roundOffLedgerId!.Value, roundOff.IsPositive ? itemSide : partySide, roundOff.Abs()));
        }

        return new InvoiceDraft(accounting, itemSpecs, taxableTotal, cgst, stateTax, igst, cess, roundOff, grandTotal);
    }
}
