using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Abstractions;
using TallyNext.Application.Common;
using TallyNext.Application.Security;
using TallyNext.Application.Sync;
using TallyNext.Contracts.Inventory;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Inventory;

namespace TallyNext.Application.Inventory;

/// <summary>Inventory masters: units of measure, godowns and stock items.</summary>
public sealed class InventoryService(IAccountingDbContext db, IAccessGuard guard, IAuditTrail audit)
{
    // ------------------------------------------------------------------ defaults

    /// <summary>
    /// Creates the default unit (Nos) and the Main Location godown when the company has none - for companies created
    /// before inventory was available. Idempotent; returns false when nothing was missing.
    /// </summary>
    public async Task<bool> EnsureDefaultsAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.MasterEdit, cancellationToken);
        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        var hasUnits = await db.Units.AnyAsync(u => u.CompanyId == companyId && !u.IsDeleted, cancellationToken);
        var hasMain = await db.Godowns.AnyAsync(g => g.CompanyId == companyId && g.IsSystem && !g.IsDeleted, cancellationToken);
        if (hasUnits && hasMain)
        {
            return false;
        }

        var (units, godowns) = DefaultInventory.Build(companyId);
        if (!hasUnits)
        {
            foreach (var unit in units)
            {
                db.Units.Add(unit);
                audit.Record(companyId, "unit.created", "unit", unit.Id, $"Default unit '{unit.Symbol}' created", after: SnapshotMapper.ToSnapshot(unit));
            }
        }

        if (!hasMain)
        {
            foreach (var godown in godowns)
            {
                if (await db.Godowns.AnyAsync(g => g.CompanyId == companyId && !g.IsDeleted && g.NormalizedName == godown.NormalizedName, cancellationToken))
                {
                    throw new ConflictException("godown.duplicate_name", $"A godown named '{godown.Name}' already exists; rename it first.");
                }

                db.Godowns.Add(godown);
                audit.Record(companyId, "godown.created", "godown", godown.Id, $"Default godown '{godown.Name}' created", after: SnapshotMapper.ToSnapshot(godown));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return true;
    }

    // ------------------------------------------------------------------ units

    public async Task<IReadOnlyList<UnitDto>> ListUnitsAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.MasterView, cancellationToken);
        return await db.Units.AsNoTracking()
            .Where(u => u.CompanyId == companyId && !u.IsDeleted)
            .OrderBy(u => u.NormalizedSymbol)
            .Select(u => new UnitDto(u.Id, u.Symbol, u.Name, u.DecimalPlaces, u.Version))
            .ToListAsync(cancellationToken);
    }

    public async Task<UnitDto> SaveUnitAsync(Guid companyId, Guid? unitId, UnitInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await guard.RequireAsync(companyId, Permission.MasterEdit, cancellationToken);
        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);

        UnitOfMeasure unit;
        object? before = null;
        if (unitId is { } id)
        {
            unit = await db.Units.SingleOrDefaultAsync(u => u.CompanyId == companyId && u.Id == id && !u.IsDeleted, cancellationToken)
                ?? throw new NotFoundException("unit");
            before = SnapshotMapper.ToSnapshot(unit);
            if (unit.DecimalPlaces > input.DecimalPlaces && await IsUnitUsedAsync(companyId, unit.Id, cancellationToken))
            {
                throw new ConflictException("unit.decimals_in_use", "Decimal places cannot be reduced for a unit already used by stock items.");
            }

            unit.Update(input.Symbol, input.Name, input.DecimalPlaces);
        }
        else
        {
            var newId = input.Id ?? Ids.New();
            if (await db.Units.AsNoTracking().AnyAsync(u => u.Id == newId, cancellationToken))
            {
                return await GetUnitAsync(companyId, newId, cancellationToken);
            }

            unit = UnitOfMeasure.Create(newId, companyId, input.Symbol, input.Name, input.DecimalPlaces);
            db.Units.Add(unit);
        }

        if (await db.Units.AnyAsync(u => u.CompanyId == companyId && !u.IsDeleted && u.Id != unit.Id && u.NormalizedSymbol == unit.NormalizedSymbol, cancellationToken))
        {
            throw new ConflictException("unit.duplicate_symbol", "A unit with this symbol already exists.");
        }

        audit.Record(companyId, before is null ? "unit.created" : "unit.updated", "unit", unit.Id, $"Unit '{unit.Symbol}' saved", before, SnapshotMapper.ToSnapshot(unit));
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new UnitDto(unit.Id, unit.Symbol, unit.Name, unit.DecimalPlaces, unit.Version);
    }

    // ------------------------------------------------------------------ godowns

    public async Task<IReadOnlyList<GodownDto>> ListGodownsAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.MasterView, cancellationToken);
        return await db.Godowns.AsNoTracking()
            .Where(g => g.CompanyId == companyId && !g.IsDeleted)
            .OrderByDescending(g => g.IsSystem).ThenBy(g => g.NormalizedName)
            .Select(g => new GodownDto(g.Id, g.Name, g.IsSystem, g.Version))
            .ToListAsync(cancellationToken);
    }

    public async Task<GodownDto> SaveGodownAsync(Guid companyId, Guid? godownId, GodownInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await guard.RequireAsync(companyId, Permission.MasterEdit, cancellationToken);
        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);

        Godown godown;
        object? before = null;
        if (godownId is { } id)
        {
            godown = await db.Godowns.SingleOrDefaultAsync(g => g.CompanyId == companyId && g.Id == id && !g.IsDeleted, cancellationToken)
                ?? throw new NotFoundException("godown");
            before = SnapshotMapper.ToSnapshot(godown);
            godown.Rename(input.Name);
        }
        else
        {
            var newId = input.Id ?? Ids.New();
            var existing = await db.Godowns.AsNoTracking().SingleOrDefaultAsync(g => g.Id == newId, cancellationToken);
            if (existing is not null)
            {
                return new GodownDto(existing.Id, existing.Name, existing.IsSystem, existing.Version);
            }

            godown = Godown.Create(newId, companyId, input.Name);
            db.Godowns.Add(godown);
        }

        if (await db.Godowns.AnyAsync(g => g.CompanyId == companyId && !g.IsDeleted && g.Id != godown.Id && g.NormalizedName == godown.NormalizedName, cancellationToken))
        {
            throw new ConflictException("godown.duplicate_name", "A godown with this name already exists.");
        }

        audit.Record(companyId, before is null ? "godown.created" : "godown.updated", "godown", godown.Id, $"Godown '{godown.Name}' saved", before, SnapshotMapper.ToSnapshot(godown));
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new GodownDto(godown.Id, godown.Name, godown.IsSystem, godown.Version);
    }

    // ------------------------------------------------------------------ stock items

    public async Task<IReadOnlyList<StockItemDto>> ListItemsAsync(Guid companyId, string? search = null, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.MasterView, cancellationToken);
        var query = db.StockItems.AsNoTracking().Where(i => i.CompanyId == companyId && !i.IsDeleted);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = NameRules.Normalize(search.Trim());
            query = query.Where(i => i.NormalizedName.Contains(term));
        }

        var items = await query.OrderBy(i => i.NormalizedName).Take(2000).ToListAsync(cancellationToken);
        var units = await UnitMapAsync(companyId, cancellationToken);
        return items.Select(i => ToDto(i, units)).ToList();
    }

    /// <summary>All active items with unit and GST schedule, for invoice entry pickers (cached by the UI).</summary>
    public async Task<IReadOnlyList<StockItemLookup>> LookupItemsAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.MasterView, cancellationToken);
        var units = await UnitMapAsync(companyId, cancellationToken);
        var items = await db.StockItems.AsNoTracking()
            .Where(i => i.CompanyId == companyId && !i.IsDeleted)
            .OrderBy(i => i.NormalizedName)
            .ToListAsync(cancellationToken);
        return items.Select(i =>
        {
            var unit = units.GetValueOrDefault(i.UnitId);
            return new StockItemLookup(i.Id, i.Name, i.Alias, unit?.Symbol ?? "?", unit?.DecimalPlaces ?? 0, i.GstRates);
        }).ToList();
    }

    public async Task<StockItemDto> GetItemAsync(Guid companyId, Guid itemId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.MasterView, cancellationToken);
        var item = await db.StockItems.AsNoTracking().SingleOrDefaultAsync(i => i.CompanyId == companyId && i.Id == itemId && !i.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("stock_item");
        return ToDto(item, await UnitMapAsync(companyId, cancellationToken));
    }

    public async Task<StockItemDto> SaveItemAsync(Guid companyId, Guid? itemId, StockItemInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await guard.RequireAsync(companyId, Permission.MasterEdit, cancellationToken);
        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);

        var unit = await db.Units.SingleOrDefaultAsync(u => u.CompanyId == companyId && u.Id == input.UnitId && !u.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("unit");
        Godown? godown = input.OpeningGodownId is { } godownId
            ? await db.Godowns.SingleOrDefaultAsync(g => g.CompanyId == companyId && g.Id == godownId && !g.IsDeleted, cancellationToken) ?? throw new NotFoundException("godown")
            : null;
        long openingQuantity;
        try
        {
            openingQuantity = StockMath.ToQuantityMilli(input.OpeningQuantity, unit.DecimalPlaces);
        }
        catch (DomainException ex)
        {
            throw new ValidationFailedException(ex.Code, ex.Message, new Dictionary<string, string[]> { [nameof(input.OpeningQuantity)] = [ex.Message] });
        }

        var openingValue = Amounts.Parse(input.OpeningValue, nameof(input.OpeningValue));
        var rates = input.GstRates.Select(r => r.ToEntry()).ToList();

        StockItem item;
        object? before = null;
        if (itemId is { } id)
        {
            item = await db.StockItems.SingleOrDefaultAsync(i => i.CompanyId == companyId && i.Id == id && !i.IsDeleted, cancellationToken)
                ?? throw new NotFoundException("stock_item");
            if (input.ExpectedVersion is { } expected && expected != item.Version)
            {
                throw new ConflictException("stock_item.version_conflict", "The item was changed by someone else. Reload and try again.", item.Version);
            }

            before = SnapshotMapper.ToSnapshot(item);
            item.Update(input.Name, unit, input.ValuationMethod, rates, openingQuantity, openingValue, godown, input.Alias, input.PartNumber);
        }
        else
        {
            var newId = input.Id ?? Ids.New();
            if (await db.StockItems.AsNoTracking().AnyAsync(i => i.Id == newId, cancellationToken))
            {
                return await GetItemAsync(companyId, newId, cancellationToken);
            }

            item = StockItem.Create(newId, companyId, input.Name, unit, input.ValuationMethod, rates, openingQuantity, openingValue, godown, input.Alias, input.PartNumber);
            db.StockItems.Add(item);
        }

        if (await db.StockItems.AnyAsync(i => i.CompanyId == companyId && !i.IsDeleted && i.Id != item.Id && i.NormalizedName == item.NormalizedName, cancellationToken))
        {
            throw new ConflictException("stock_item.duplicate_name", "A stock item with this name already exists.");
        }

        audit.Record(companyId, before is null ? "stock_item.created" : "stock_item.updated", "stock-item", item.Id, $"Stock item '{item.Name}' saved", before, SnapshotMapper.ToSnapshot(item));
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return ToDto(item, new Dictionary<Guid, UnitOfMeasure> { [unit.Id] = unit });
    }

    public async Task DeleteItemAsync(Guid companyId, Guid itemId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.MasterEdit, cancellationToken);
        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        var item = await db.StockItems.SingleOrDefaultAsync(i => i.CompanyId == companyId && i.Id == itemId && !i.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("stock_item");
        if (await db.VoucherItemLines.AnyAsync(l => l.CompanyId == companyId && l.StockItemId == itemId, cancellationToken))
        {
            throw new ConflictException("stock_item.in_use", "This item is used in vouchers and cannot be deleted.");
        }

        var before = SnapshotMapper.ToSnapshot(item);
        item.MarkDeleted();
        audit.Record(companyId, "stock_item.deleted", "stock-item", item.Id, $"Stock item '{item.Name}' deleted", before);
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
    }

    private async Task<UnitDto> GetUnitAsync(Guid companyId, Guid unitId, CancellationToken cancellationToken) =>
        await db.Units.AsNoTracking()
            .Where(u => u.CompanyId == companyId && u.Id == unitId)
            .Select(u => new UnitDto(u.Id, u.Symbol, u.Name, u.DecimalPlaces, u.Version))
            .SingleOrDefaultAsync(cancellationToken)
        ?? throw new ConflictException("unit.id_conflict", "A unit with this identifier already exists.");

    private Task<bool> IsUnitUsedAsync(Guid companyId, Guid unitId, CancellationToken cancellationToken) =>
        db.StockItems.AnyAsync(i => i.CompanyId == companyId && i.UnitId == unitId && !i.IsDeleted, cancellationToken);

    private async Task<Dictionary<Guid, UnitOfMeasure>> UnitMapAsync(Guid companyId, CancellationToken cancellationToken) =>
        await db.Units.AsNoTracking().Where(u => u.CompanyId == companyId).ToDictionaryAsync(u => u.Id, cancellationToken);

    private static StockItemDto ToDto(StockItem i, IReadOnlyDictionary<Guid, UnitOfMeasure> units)
    {
        var unit = units.GetValueOrDefault(i.UnitId);
        return new StockItemDto(
            i.Id, i.Name, i.Alias, i.PartNumber, i.UnitId, unit?.Symbol ?? "?", unit?.DecimalPlaces ?? 0, i.ValuationMethod, i.GstRates,
            StockMath.ToQuantity(i.OpeningQuantityMilli), Amounts.ToDecimal(i.OpeningValueMinor), i.OpeningGodownId, i.Version);
    }
}
