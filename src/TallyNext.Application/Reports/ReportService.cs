using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Abstractions;
using TallyNext.Application.Common;
using TallyNext.Application.Inventory;
using TallyNext.Application.Security;
using TallyNext.Application.Vouchers;
using TallyNext.Contracts;
using TallyNext.Contracts.Reports;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Inventory;
using TallyNext.Domain.Tax;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Application.Reports;

/// <summary>
/// Financial reports derived on demand from the authoritative data: ledger opening balances (as at the books
/// beginning date) plus posted voucher lines. Nothing here is stored, so reports cannot drift from the books.
/// </summary>
/// <remarks>
/// <para>Sign convention: a signed amount is debit minus credit. Asset and expense balances are normally positive,
/// liability, capital and income balances normally negative. Presentation splits signed values into non-negative
/// Dr and Cr columns.</para>
/// <para>Income and expense ledgers are period accounts: at each financial-year start their accumulated balance is
/// carried into the Profit &amp; Loss ledger (computationally - no closing entries are written).</para>
/// <para>All aggregation happens in the database with grouped conditional sums over the index
/// (CompanyId, LedgerId, VoucherDate); only per-ledger totals (one row per ledger) come back to memory.</para>
/// </remarks>
public sealed class ReportService(IAccountingDbContext db, IAccessGuard guard, StockValuationService stockValuation)
{
    // ------------------------------------------------------------------ Day Book

    public async Task<DayBookReport> DayBookAsync(
        Guid companyId, DateOnly from, DateOnly to, int page = 1, int pageSize = Paging.DefaultPageSize, bool includeDrafts = false,
        CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.ReportView, cancellationToken);
        EnsureRange(from, to);
        (page, pageSize) = Paging.Normalize(page, pageSize);

        var vouchers = db.Vouchers.AsNoTracking().Where(v => v.CompanyId == companyId && !v.IsDeleted && v.Date >= from && v.Date <= to);
        vouchers = includeDrafts
            ? vouchers.Where(v => v.Status != VoucherStatus.Cancelled)
            : vouchers.Where(v => v.Status == VoucherStatus.Posted);

        var total = await vouchers.LongCountAsync(cancellationToken);
        var periodTotal = await vouchers.Where(v => v.Status == VoucherStatus.Posted).SumAsync(v => v.TotalMinor, cancellationToken);
        var rows = await vouchers
            .OrderBy(v => v.Date).ThenBy(v => v.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(v => new { v.Id, v.Date, v.Number, v.VoucherTypeId, v.BaseType, v.Status, v.TotalMinor, v.Narration, v.ReversalOfVoucherId })
            .ToListAsync(cancellationToken);

        var particulars = await VoucherReadHelpers.FirstLineParticularsAsync(db, companyId, rows.Select(r => r.Id).ToList(), cancellationToken);
        var typeNames = await VoucherReadHelpers.VoucherTypeNamesAsync(db, companyId, cancellationToken);
        var items = rows.Select(r =>
        {
            var (name, side) = particulars.TryGetValue(r.Id, out var p) ? p : (string.Empty, DrCr.Debit);
            var amount = Amounts.ToDecimal(r.TotalMinor);
            return new DayBookRow(
                r.Id, r.Date, r.Number, typeNames.GetValueOrDefault(r.VoucherTypeId, "?"), r.BaseType, r.Status, name,
                side == DrCr.Debit ? amount : 0m, side == DrCr.Credit ? amount : 0m, r.Narration, r.ReversalOfVoucherId is not null);
        }).ToList();

        return new DayBookReport(from, to, new PagedResult<DayBookRow>(items, page, pageSize, total), Amounts.ToDecimal(periodTotal));
    }

    // ------------------------------------------------------------------ Ledger

    /// <summary>Ledger account with opening balance, paged postings with running balance, and closing balance.</summary>
    public async Task<LedgerReport> LedgerAsync(
        Guid companyId, Guid ledgerId, DateOnly from, DateOnly to, int page = 1, int pageSize = Paging.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.ReportView, cancellationToken);
        EnsureRange(from, to);
        (page, pageSize) = Paging.Normalize(page, pageSize);

        var company = await LoadCompanyAsync(companyId, cancellationToken);
        var ledger = await db.Ledgers.AsNoTracking().SingleOrDefaultAsync(l => l.CompanyId == companyId && l.Id == ledgerId && !l.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("ledger");
        var group = await db.AccountGroups.AsNoTracking().SingleAsync(g => g.Id == ledger.GroupId, cancellationToken);

        // Income/expense ledgers restart at each financial-year start; balance-sheet ledgers carry forward.
        var isPeriodAccount = group.Nature.IsProfitAndLoss();
        var openingFloor = isPeriodAccount ? company.FinancialYearOf(from).Start : DateOnly.MinValue;

        var lines = db.VoucherLines.AsNoTracking().Where(l => l.CompanyId == companyId && l.LedgerId == ledgerId && l.IsPosted);
        var before = await lines
            .Where(l => l.VoucherDate < from && l.VoucherDate >= openingFloor)
            .SumAsync(l => l.Side == DrCr.Debit ? l.AmountMinor : -l.AmountMinor, cancellationToken);
        var opening = (isPeriodAccount ? 0 : ledger.SignedOpeningMinor) + before;

        var period = lines.Where(l => l.VoucherDate >= from && l.VoucherDate <= to);
        var periodDebit = await period.SumAsync(l => l.Side == DrCr.Debit ? l.AmountMinor : 0L, cancellationToken);
        var periodCredit = await period.SumAsync(l => l.Side == DrCr.Credit ? l.AmountMinor : 0L, cancellationToken);
        var count = await period.LongCountAsync(cancellationToken);

        var ordered = period.OrderBy(l => l.VoucherDate).ThenBy(l => l.VoucherId).ThenBy(l => l.LineNo);
        var skip = (page - 1) * pageSize;
        var running = opening + (skip == 0
            ? 0
            : await ordered.Take(skip).SumAsync(l => l.Side == DrCr.Debit ? l.AmountMinor : -l.AmountMinor, cancellationToken));

        var pageLines = await ordered.Skip(skip).Take(pageSize)
            .Select(l => new { l.Id, l.VoucherId, l.VoucherDate, l.Side, l.AmountMinor, l.Narration })
            .ToListAsync(cancellationToken);

        var voucherIds = pageLines.Select(l => l.VoucherId).Distinct().ToList();
        var vouchers = await db.Vouchers.AsNoTracking()
            .Where(v => v.CompanyId == companyId && voucherIds.Contains(v.Id))
            .Select(v => new { v.Id, v.Number, v.VoucherTypeId, v.Narration })
            .ToDictionaryAsync(v => v.Id, cancellationToken);
        var counterLines = await (
                from l in db.VoucherLines.AsNoTracking()
                join led in db.Ledgers.AsNoTracking() on l.LedgerId equals led.Id
                where l.CompanyId == companyId && voucherIds.Contains(l.VoucherId)
                select new { l.VoucherId, l.LedgerId, led.Name, l.Side, l.AmountMinor })
            .ToListAsync(cancellationToken);
        var typeNames = await VoucherReadHelpers.VoucherTypeNamesAsync(db, companyId, cancellationToken);

        var rows = new List<LedgerReportRow>(pageLines.Count);
        foreach (var line in pageLines)
        {
            running += line.Side == DrCr.Debit ? line.AmountMinor : -line.AmountMinor;
            var voucher = vouchers[line.VoucherId];
            var opposite = counterLines
                .Where(c => c.VoucherId == line.VoucherId && c.Side != line.Side && c.LedgerId != ledgerId)
                .OrderByDescending(c => c.AmountMinor)
                .ToList();
            var particulars = opposite.Count switch
            {
                0 => ledger.Name,
                1 => opposite[0].Name,
                _ => $"{opposite[0].Name} (+{opposite.Count - 1} more)",
            };
            rows.Add(new LedgerReportRow(
                line.VoucherId, line.Id, line.VoucherDate, voucher.Number, typeNames.GetValueOrDefault(voucher.VoucherTypeId, "?"),
                particulars, line.Narration ?? voucher.Narration,
                line.Side == DrCr.Debit ? Amounts.ToDecimal(line.AmountMinor) : 0m,
                line.Side == DrCr.Credit ? Amounts.ToDecimal(line.AmountMinor) : 0m,
                Amounts.DebitPart(running), Amounts.CreditPart(running)));
        }

        var closing = opening + periodDebit - periodCredit;
        return new LedgerReport(
            ledger.Id, ledger.Name, group.Name, from, to,
            Amounts.DebitPart(opening), Amounts.CreditPart(opening),
            Amounts.ToDecimal(periodDebit), Amounts.ToDecimal(periodCredit),
            Amounts.DebitPart(closing), Amounts.CreditPart(closing),
            new PagedResult<LedgerReportRow>(rows, page, pageSize, count));
    }

    // ------------------------------------------------------------------ Trial balance

    public async Task<TrialBalanceReport> TrialBalanceAsync(
        Guid companyId, DateOnly from, DateOnly to, bool includeZeroBalances = false, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.ReportView, cancellationToken);
        EnsureRange(from, to);
        var books = await LoadBooksAsync(companyId, from, to, cancellationToken);

        // Inventory (periodic method): the trial balance shows the stock at the start of the financial year as
        // "Opening stock"; the change in stock during earlier years is part of the profit carried into the
        // Profit & Loss ledger, and stock at books beginning is part of the opening balances.
        var fyStart = (await LoadCompanyAsync(companyId, cancellationToken)).FinancialYearOf(from).Start;
        var stock = await stockValuation.TotalsAsync(companyId, fyStart, to, cancellationToken);
        var openingStock = stock.OpeningAtFromMinor;
        var priorStockChange = stock.OpeningAtFromMinor - stock.BooksOpeningMinor;
        var profitLedger = books.Ledgers.Values.FirstOrDefault(l => l.SystemKey == SystemLedgerKeys.ProfitAndLoss);
        if (profitLedger is not null && priorStockChange != 0)
        {
            books.Ledgers[profitLedger.Id] = profitLedger with { Opening = profitLedger.Opening - priorStockChange };
        }

        var rows = new List<TrialBalanceRow>();
        long totalOpeningDr = Math.Max(openingStock, 0), totalOpeningCr = Math.Max(-openingStock, 0), totalDr = 0, totalCr = 0;
        long totalClosingDr = Math.Max(openingStock, 0), totalClosingCr = Math.Max(-openingStock, 0), openingDifference = stock.BooksOpeningMinor;
        foreach (var l in books.Ledgers.Values)
        {
            openingDifference += l.SignedOpeningMinor;
            totalOpeningDr += Math.Max(l.Opening, 0);
            totalOpeningCr += Math.Max(-l.Opening, 0);
            totalDr += l.PeriodDebit;
            totalCr += l.PeriodCredit;
            totalClosingDr += Math.Max(l.Closing, 0);
            totalClosingCr += Math.Max(-l.Closing, 0);
        }

        // Groups (with roll-ups), then their sub-groups, then their own ledgers - depth-first.
        var ledgersByGroup = books.Ledgers.Values.ToLookup(l => l.GroupId);
        var children = books.Hierarchy.All.Where(g => !g.IsDeleted).ToLookup(g => g.ParentGroupId);

        (long Opening, long Dr, long Cr, long Closing, List<TrialBalanceRow> Rows) Visit(AccountGroup group, int depth)
        {
            var childRows = new List<TrialBalanceRow>();
            long opening = 0, dr = 0, cr = 0, closing = 0;
            foreach (var sub in children[group.Id].OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                var s = Visit(sub, depth + 1);
                opening += s.Opening;
                dr += s.Dr;
                cr += s.Cr;
                closing += s.Closing;
                childRows.AddRange(s.Rows);
            }

            foreach (var l in ledgersByGroup[group.Id].OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
            {
                opening += l.Opening;
                dr += l.PeriodDebit;
                cr += l.PeriodCredit;
                closing += l.Closing;
                if (includeZeroBalances || l.Opening != 0 || l.PeriodDebit != 0 || l.PeriodCredit != 0 || l.Closing != 0)
                {
                    childRows.Add(Row(l.Id, l.Name, false, group.Id, depth + 1, group.Nature, l.Opening, l.PeriodDebit, l.PeriodCredit, l.Closing));
                }
            }

            if (group.SystemKey == SystemGroupKeys.StockInHand && openingStock != 0)
            {
                opening += openingStock;
                closing += openingStock;
                childRows.Add(Row(Guid.Empty, "Opening stock", false, group.Id, depth + 1, group.Nature, openingStock, 0, 0, openingStock));
            }

            var rowsOut = new List<TrialBalanceRow>(childRows.Count + 1);
            if (includeZeroBalances || childRows.Count > 0 || opening != 0 || dr != 0 || cr != 0 || closing != 0)
            {
                rowsOut.Add(Row(group.Id, group.Name, true, group.ParentGroupId, depth, group.Nature, opening, dr, cr, closing));
                rowsOut.AddRange(childRows);
            }

            return (opening, dr, cr, closing, rowsOut);
        }

        foreach (var primary in children[null].OrderBy(g => g.Nature).ThenBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.AddRange(Visit(primary, 0).Rows);
        }

        return new TrialBalanceReport(
            from, to, rows,
            Amounts.ToDecimal(totalOpeningDr), Amounts.ToDecimal(totalOpeningCr),
            Amounts.ToDecimal(totalDr), Amounts.ToDecimal(totalCr),
            Amounts.ToDecimal(totalClosingDr), Amounts.ToDecimal(totalClosingCr),
            Amounts.ToDecimal(openingDifference));
    }

    /// <summary>Cash and bank ledgers with their opening, receipts, payments and closing for the period.</summary>
    public async Task<IReadOnlyList<TrialBalanceRow>> CashAndBankSummaryAsync(Guid companyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.ReportView, cancellationToken);
        EnsureRange(from, to);
        var books = await LoadBooksAsync(companyId, from, to, cancellationToken);
        return books.Ledgers.Values
            .Where(l => books.Hierarchy.TraitsOf(l.GroupId).HasFlag(LedgerTraits.CashOrBank))
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .Select(l => Row(l.Id, l.Name, false, l.GroupId, 0, AccountNature.Asset, l.Opening, l.PeriodDebit, l.PeriodCredit, l.Closing))
            .ToList();
    }

    // ------------------------------------------------------------------ Profit and loss

    public async Task<ProfitAndLossReport> ProfitAndLossAsync(Guid companyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.ReportView, cancellationToken);
        EnsureRange(from, to);
        var books = await LoadBooksAsync(companyId, from, to, cancellationToken);

        // Period movement of each income/expense ledger, expressed as a positive "normal" amount.
        var amounts = books.Ledgers.Values
            .Where(l => l.Nature.IsProfitAndLoss())
            .ToDictionary(l => l.Id, l => l.Nature == AccountNature.Income ? l.PeriodCredit - l.PeriodDebit : l.PeriodDebit - l.PeriodCredit);

        var primaries = books.Hierarchy.All.Where(g => g.IsPrimary && !g.IsDeleted).ToList();
        var tradingIncome = BuildSection("Trading income", books, primaries.Where(g => g.Nature == AccountNature.Income && g.AffectsGrossProfit), amounts);
        var directCosts = BuildSection("Direct costs", books, primaries.Where(g => g.Nature == AccountNature.Expense && g.AffectsGrossProfit), amounts);
        var indirectIncomes = BuildSection("Indirect incomes", books, primaries.Where(g => g.Nature == AccountNature.Income && !g.AffectsGrossProfit), amounts);
        var indirectExpenses = BuildSection("Indirect expenses", books, primaries.Where(g => g.Nature == AccountNature.Expense && !g.AffectsGrossProfit), amounts);

        // Trading account: gross profit = sales + direct incomes + closing stock - opening stock - purchases - direct expenses.
        var stock = await stockValuation.TotalsAsync(companyId, from, to, cancellationToken);
        var gross = tradingIncome.Total - directCosts.Total + stock.ClosingAtToMinor - stock.OpeningAtFromMinor;
        var net = gross + indirectIncomes.Total - indirectExpenses.Total;
        return new ProfitAndLossReport(
            from, to,
            ToSection(tradingIncome), ToSection(directCosts), Amounts.ToDecimal(gross),
            ToSection(indirectIncomes), ToSection(indirectExpenses), Amounts.ToDecimal(net),
            ExcludesInventory: false,
            OpeningStock: Amounts.ToDecimal(stock.OpeningAtFromMinor),
            ClosingStock: Amounts.ToDecimal(stock.ClosingAtToMinor));
    }

    // ------------------------------------------------------------------ Balance sheet

    public async Task<BalanceSheetReport> BalanceSheetAsync(Guid companyId, DateOnly asAt, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.ReportView, cancellationToken);
        var company = await LoadCompanyAsync(companyId, cancellationToken);
        var fyStart = company.FinancialYearOf(asAt).Start;
        var books = await LoadBooksAsync(companyId, fyStart, asAt, cancellationToken);

        // Lifetime closing balances (opening + every posting to the date), independent of the FY carry-forward.
        long netProfitToDate = 0, currentPeriodProfit = 0, openingDifference = 0, profitLedgerBalance = 0;
        var balanceSheetAmounts = new Dictionary<Guid, long>();
        foreach (var l in books.Ledgers.Values)
        {
            openingDifference += l.SignedOpeningMinor;
            var lifetimeSigned = l.SignedOpeningMinor + l.LifetimeSigned;
            if (l.Nature.IsProfitAndLoss())
            {
                netProfitToDate -= lifetimeSigned;
                currentPeriodProfit -= l.PeriodDebit - l.PeriodCredit + l.FyToFromSigned;
                continue;
            }

            if (l.SystemKey == SystemLedgerKeys.ProfitAndLoss)
            {
                profitLedgerBalance = -lifetimeSigned;
                continue;
            }

            balanceSheetAmounts[l.Id] = l.Nature == AccountNature.Liability ? -lifetimeSigned : lifetimeSigned;
        }

        // Closing stock is an asset; the change in stock since books beginning is profit (this year's part since the
        // start of the financial year); stock at books beginning is part of the opening balances.
        var stock = await stockValuation.TotalsAsync(companyId, fyStart, asAt, cancellationToken);
        netProfitToDate += stock.ClosingAtToMinor - stock.BooksOpeningMinor;
        currentPeriodProfit += stock.ClosingAtToMinor - stock.OpeningAtFromMinor;
        openingDifference += stock.BooksOpeningMinor;
        var stockGroup = books.Hierarchy.All.FirstOrDefault(g => g.SystemKey == SystemGroupKeys.StockInHand && !g.IsDeleted);
        var synthetic = stockGroup is not null && stock.ClosingAtToMinor != 0
            ? new Dictionary<Guid, (string Name, long Amount)> { [stockGroup.Id] = ("Closing stock", stock.ClosingAtToMinor) }
            : null;

        var primaries = books.Hierarchy.All.Where(g => g.IsPrimary && !g.IsDeleted).ToList();
        var liabilities = BuildSection("Liabilities", books, primaries.Where(g => g.Nature == AccountNature.Liability), balanceSheetAmounts);
        var assets = BuildSection("Assets", books, primaries.Where(g => g.Nature == AccountNature.Asset), balanceSheetAmounts, synthetic);

        var liabilityLines = liabilities.Lines.ToList();
        var accumulated = checked(profitLedgerBalance + netProfitToDate);
        liabilityLines.Add(new StatementLine("Profit & Loss A/c", null, null, 0, Amounts.ToDecimal(accumulated)));
        liabilityLines.Add(new StatementLine("Opening balance", null, null, 1, Amounts.ToDecimal(accumulated - currentPeriodProfit)));
        liabilityLines.Add(new StatementLine("Current period", null, null, 1, Amounts.ToDecimal(currentPeriodProfit)));
        var totalLiabilities = liabilities.Total + accumulated;

        var assetLines = assets.Lines.ToList();
        var totalAssets = assets.Total;
        if (openingDifference > 0)
        {
            liabilityLines.Add(new StatementLine("Difference in opening balances", null, null, 0, Amounts.ToDecimal(openingDifference)));
            totalLiabilities += openingDifference;
        }
        else if (openingDifference < 0)
        {
            assetLines.Add(new StatementLine("Difference in opening balances", null, null, 0, Amounts.ToDecimal(-openingDifference)));
            totalAssets -= openingDifference;
        }

        return new BalanceSheetReport(
            asAt,
            new StatementSection("Liabilities", liabilityLines, Amounts.ToDecimal(totalLiabilities)),
            new StatementSection("Assets", assetLines, Amounts.ToDecimal(totalAssets)),
            Amounts.ToDecimal(accumulated),
            Amounts.ToDecimal(currentPeriodProfit),
            Amounts.ToDecimal(openingDifference),
            Amounts.ToDecimal(totalLiabilities),
            Amounts.ToDecimal(totalAssets));
    }

    // ------------------------------------------------------------------ Inventory and GST

    /// <summary>Quantity and value per stock item: opening at <paramref name="from"/>, inwards, outwards at cost, closing at <paramref name="to"/>.</summary>
    public async Task<StockSummaryReport> StockSummaryAsync(Guid companyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.ReportView, cancellationToken);
        EnsureRange(from, to);
        var valuations = await stockValuation.ValueAsync(companyId, from, to, cancellationToken);
        var items = await db.StockItems.AsNoTracking()
            .Where(i => i.CompanyId == companyId && !i.IsDeleted)
            .Select(i => new { i.Id, i.Name, i.UnitId })
            .ToDictionaryAsync(i => i.Id, cancellationToken);
        var units = await db.Units.AsNoTracking().Where(u => u.CompanyId == companyId).ToDictionaryAsync(u => u.Id, u => u.Symbol, cancellationToken);
        var godowns = await db.Godowns.AsNoTracking().Where(g => g.CompanyId == companyId).ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);

        var rows = valuations
            .Where(v => items.ContainsKey(v.StockItemId))
            .Select(v =>
            {
                var item = items[v.StockItemId];
                var r = v.Result;
                var byGodown = v.ClosingQuantityByGodown
                    .Where(g => g.Value != 0)
                    .Select(g => new GodownQuantity(g.Key, godowns.GetValueOrDefault(g.Key, "(unknown godown)"), StockMath.ToQuantity(g.Value)))
                    .OrderBy(g => g.GodownName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new StockSummaryRow(
                    item.Id, item.Name, units.GetValueOrDefault(item.UnitId, ""),
                    StockMath.ToQuantity(r.Opening.QuantityMilli), Amounts.ToDecimal(r.Opening.ValueMinor),
                    StockMath.ToQuantity(r.InwardQuantityMilli), Amounts.ToDecimal(r.InwardValueMinor),
                    StockMath.ToQuantity(r.OutwardQuantityMilli), Amounts.ToDecimal(r.OutwardCostMinor),
                    StockMath.ToQuantity(r.Closing.QuantityMilli), Amounts.ToDecimal(r.Closing.ValueMinor),
                    r.WentNegative, byGodown);
            })
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new StockSummaryReport(
            from, to, rows,
            Amounts.ToDecimal(valuations.Sum(v => v.Result.Opening.ValueMinor)),
            Amounts.ToDecimal(valuations.Sum(v => v.Result.Closing.ValueMinor)));
    }

    /// <summary>
    /// Taxable value and tax per rate for outward supplies (sales less credit notes) and inward supplies (purchases
    /// less debit notes) of posted invoices dated in the period; reversals net off the vouchers they reverse.
    /// </summary>
    public async Task<GstSummaryReport> GstSummaryAsync(Guid companyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.ReportView, cancellationToken);
        EnsureRange(from, to);
        var buckets = await (
                from l in db.VoucherItemLines.AsNoTracking()
                join v in db.Vouchers.AsNoTracking() on l.VoucherId equals v.Id
                where l.CompanyId == companyId && l.IsPosted && l.VoucherDate >= @from && l.VoucherDate <= to && v.SupplyNature != null
                group l by new { v.BaseType, IsReversal = v.ReversalOfVoucherId != null, Nature = v.SupplyNature!.Value, l.Taxability, l.IgstMilliPercent } into g
                select new
                {
                    g.Key.BaseType,
                    g.Key.IsReversal,
                    g.Key.Nature,
                    g.Key.Taxability,
                    g.Key.IgstMilliPercent,
                    Taxable = g.Sum(x => x.TaxableMinor),
                    Cgst = g.Sum(x => x.CgstMinor),
                    StateTax = g.Sum(x => x.StateTaxMinor),
                    Igst = g.Sum(x => x.IgstMinor),
                    Cess = g.Sum(x => x.CessMinor),
                })
            .ToListAsync(cancellationToken);

        var outward = new Dictionary<(Taxability, int, SupplyNature), long[]>();
        var inward = new Dictionary<(Taxability, int, SupplyNature), long[]>();
        foreach (var b in buckets)
        {
            var (target, sign) = b.BaseType switch
            {
                VoucherBaseType.Sales => (outward, 1),
                VoucherBaseType.CreditNote => (outward, -1),
                VoucherBaseType.Purchase => (inward, 1),
                VoucherBaseType.DebitNote => (inward, -1),
                _ => (null, 0),
            };
            if (target is null)
            {
                continue;
            }

            if (b.IsReversal)
            {
                sign = -sign;
            }

            var key = (b.Taxability, b.IgstMilliPercent, b.Nature);
            if (!target.TryGetValue(key, out var sums))
            {
                target[key] = sums = new long[5];
            }

            sums[0] += sign * b.Taxable;
            sums[1] += sign * b.Cgst;
            sums[2] += sign * b.StateTax;
            sums[3] += sign * b.Igst;
            sums[4] += sign * b.Cess;
        }

        static List<GstSummaryRow> ToRows(Dictionary<(Taxability, int, SupplyNature), long[]> source) => source
            .Where(kv => kv.Value.Any(x => x != 0))
            .OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2).ThenBy(kv => kv.Key.Item3)
            .Select(kv => new GstSummaryRow(
                kv.Key.Item1, kv.Key.Item2 / 1000m, kv.Key.Item3,
                Amounts.ToDecimal(kv.Value[0]), Amounts.ToDecimal(kv.Value[1]), Amounts.ToDecimal(kv.Value[2]),
                Amounts.ToDecimal(kv.Value[3]), Amounts.ToDecimal(kv.Value[4])))
            .ToList();

        static long Tax(Dictionary<(Taxability, int, SupplyNature), long[]> source) => source.Values.Sum(v => v[1] + v[2] + v[3] + v[4]);

        var outputTax = Tax(outward);
        var inputTax = Tax(inward);
        return new GstSummaryReport(
            from, to, ToRows(outward), ToRows(inward),
            Amounts.ToDecimal(outputTax), Amounts.ToDecimal(inputTax), Amounts.ToDecimal(outputTax - inputTax));
    }

    // ------------------------------------------------------------------ shared computation

    private sealed record LedgerBalances(
        Guid Id,
        string Name,
        Guid GroupId,
        AccountNature Nature,
        string? SystemKey,
        long SignedOpeningMinor,
        long Opening,
        long PeriodDebit,
        long PeriodCredit,
        long FyToFromSigned,
        long LifetimeSigned)
    {
        public long Closing => Opening + PeriodDebit - PeriodCredit;
    }

    private sealed record Books(GroupHierarchy Hierarchy, Dictionary<Guid, LedgerBalances> Ledgers);

    private sealed record Section(string Title, List<StatementLine> Lines, long Total);

    /// <summary>
    /// One grouped query over posted lines up to <paramref name="to"/> producing, per ledger: postings before the
    /// financial year of <paramref name="from"/>, postings from that year start to <paramref name="from"/>, and the
    /// period debits/credits. Opening balances apply the period-account carry-forward rule.
    /// </summary>
    private async Task<Books> LoadBooksAsync(Guid companyId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var company = await LoadCompanyAsync(companyId, cancellationToken);
        var fyStart = company.FinancialYearOf(from).Start;
        var groups = await db.AccountGroups.AsNoTracking().Where(g => g.CompanyId == companyId).ToListAsync(cancellationToken);
        var hierarchy = new GroupHierarchy(groups);
        var ledgers = await db.Ledgers.AsNoTracking()
            .Where(l => l.CompanyId == companyId && !l.IsDeleted)
            .Select(l => new { l.Id, l.Name, l.GroupId, l.SystemKey, l.OpeningBalanceMinor, l.OpeningSide })
            .ToListAsync(cancellationToken);

        var sums = await db.VoucherLines.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.IsPosted && l.VoucherDate <= to)
            .GroupBy(l => l.LedgerId)
            .Select(g => new
            {
                LedgerId = g.Key,
                BeforeFy = g.Sum(l => l.VoucherDate < fyStart ? (l.Side == DrCr.Debit ? l.AmountMinor : -l.AmountMinor) : 0L),
                FyToFrom = g.Sum(l => l.VoucherDate >= fyStart && l.VoucherDate < from ? (l.Side == DrCr.Debit ? l.AmountMinor : -l.AmountMinor) : 0L),
                PeriodDebit = g.Sum(l => l.VoucherDate >= from && l.Side == DrCr.Debit ? l.AmountMinor : 0L),
                PeriodCredit = g.Sum(l => l.VoucherDate >= from && l.Side == DrCr.Credit ? l.AmountMinor : 0L),
            })
            .ToDictionaryAsync(x => x.LedgerId, cancellationToken);

        var result = new Dictionary<Guid, LedgerBalances>(ledgers.Count);
        long carriedProfit = 0;
        foreach (var l in ledgers)
        {
            var nature = hierarchy.Get(l.GroupId).Nature;
            var signedOpening = l.OpeningSide == DrCr.Debit ? l.OpeningBalanceMinor : -l.OpeningBalanceMinor;
            sums.TryGetValue(l.Id, out var s);
            long beforeFy = s?.BeforeFy ?? 0, fyToFrom = s?.FyToFrom ?? 0, dr = s?.PeriodDebit ?? 0, cr = s?.PeriodCredit ?? 0;

            long opening;
            if (nature.IsProfitAndLoss())
            {
                opening = fyToFrom;
                carriedProfit += beforeFy;
            }
            else
            {
                opening = signedOpening + beforeFy + fyToFrom;
            }

            result[l.Id] = new LedgerBalances(l.Id, l.Name, l.GroupId, nature, l.SystemKey, signedOpening, opening, dr, cr, fyToFrom, beforeFy + fyToFrom + dr - cr);
        }

        var profitLedger = result.Values.FirstOrDefault(l => l.SystemKey == SystemLedgerKeys.ProfitAndLoss);
        if (profitLedger is not null && carriedProfit != 0)
        {
            result[profitLedger.Id] = profitLedger with { Opening = profitLedger.Opening + carriedProfit };
        }

        return new Books(hierarchy, result);
    }

    private static Section BuildSection(
        string title,
        Books books,
        IEnumerable<AccountGroup> primaries,
        IReadOnlyDictionary<Guid, long> amounts,
        IReadOnlyDictionary<Guid, (string Name, long Amount)>? synthetic = null)
    {
        var children = books.Hierarchy.All.Where(g => !g.IsDeleted).ToLookup(g => g.ParentGroupId);
        var ledgersByGroup = books.Ledgers.Values.Where(l => amounts.ContainsKey(l.Id)).ToLookup(l => l.GroupId);
        var lines = new List<StatementLine>();
        long total = 0;

        (long Total, List<StatementLine> Lines) Visit(AccountGroup group, int depth)
        {
            var inner = new List<StatementLine>();
            long subtotal = 0;
            foreach (var sub in children[group.Id].OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                var s = Visit(sub, depth + 1);
                subtotal += s.Total;
                inner.AddRange(s.Lines);
            }

            foreach (var l in ledgersByGroup[group.Id].OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
            {
                var amount = amounts[l.Id];
                subtotal += amount;
                if (amount != 0)
                {
                    inner.Add(new StatementLine(l.Name, null, l.Id, depth + 1, Amounts.ToDecimal(amount)));
                }
            }

            if (synthetic is not null && synthetic.TryGetValue(group.Id, out var extra) && extra.Amount != 0)
            {
                subtotal += extra.Amount;
                inner.Add(new StatementLine(extra.Name, null, null, depth + 1, Amounts.ToDecimal(extra.Amount)));
            }

            if (subtotal == 0 && inner.Count == 0)
            {
                return (0, []);
            }

            inner.Insert(0, new StatementLine(group.Name, group.Id, null, depth, Amounts.ToDecimal(subtotal)));
            return (subtotal, inner);
        }

        foreach (var primary in primaries.OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            var s = Visit(primary, 0);
            total += s.Total;
            lines.AddRange(s.Lines);
        }

        return new Section(title, lines, total);
    }

    private static StatementSection ToSection(Section s) => new(s.Title, s.Lines, Amounts.ToDecimal(s.Total));

    private static TrialBalanceRow Row(Guid id, string name, bool isGroup, Guid? parentId, int depth, AccountNature nature, long opening, long dr, long cr, long closing) =>
        new(id, name, isGroup, parentId, depth, nature,
            Amounts.DebitPart(opening), Amounts.CreditPart(opening),
            Amounts.ToDecimal(dr), Amounts.ToDecimal(cr),
            Amounts.DebitPart(closing), Amounts.CreditPart(closing));

    private async Task<Company> LoadCompanyAsync(Guid companyId, CancellationToken cancellationToken) =>
        await db.Companies.AsNoTracking().SingleOrDefaultAsync(c => c.Id == companyId && !c.IsDeleted, cancellationToken)
        ?? throw new NotFoundException("company");

    private static void EnsureRange(DateOnly from, DateOnly to)
    {
        if (from > to)
        {
            throw new ValidationFailedException("report.range_invalid", "The start date must be on or before the end date.");
        }
    }
}
