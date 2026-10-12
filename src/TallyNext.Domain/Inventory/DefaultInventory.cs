using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Tax;

namespace TallyNext.Domain.Inventory;

/// <summary>Masters created with every new company so stock can be entered immediately.</summary>
public static class DefaultInventory
{
    public static (IReadOnlyList<UnitOfMeasure> Units, IReadOnlyList<Godown> Godowns) Build(Guid companyId) =>
    (
        [UnitOfMeasure.Create(Ids.New(), companyId, "Nos", "Numbers", 0)],
        [Godown.Create(Ids.New(), companyId, Godown.MainLocationName, isSystem: true)]
    );
}

/// <summary>Duty and round-off ledgers created when GST is enabled for a company.</summary>
public static class DefaultTaxLedgers
{
    public const string RoundOffKey = "round-off";

    public static IReadOnlyList<Ledger> Build(AccountGroup dutiesAndTaxes, AccountGroup indirectExpenses, IReadOnlyCollection<Ledger> existing)
    {
        ArgumentNullException.ThrowIfNull(dutiesAndTaxes);
        ArgumentNullException.ThrowIfNull(indirectExpenses);
        ArgumentNullException.ThrowIfNull(existing);
        var result = new List<Ledger>();

        void Duty(string name, GstTaxType type)
        {
            if (existing.Any(l => !l.IsDeleted && l.GstTaxType == type))
            {
                return;
            }

            result.Add(Ledger.Create(Ids.New(), dutiesAndTaxes, name, Money.Zero, DrCr.Credit, new LedgerDetails(GstTaxType: type)));
        }

        Duty("CGST", GstTaxType.Cgst);
        Duty("SGST", GstTaxType.Sgst);
        Duty("UTGST", GstTaxType.Utgst);
        Duty("IGST", GstTaxType.Igst);
        Duty("GST Cess", GstTaxType.Cess);
        if (!existing.Any(l => !l.IsDeleted && l.SystemKey == RoundOffKey))
        {
            result.Add(Ledger.Create(Ids.New(), indirectExpenses, "Round Off", Money.Zero, DrCr.Debit, systemKey: RoundOffKey));
        }

        return result;
    }
}
