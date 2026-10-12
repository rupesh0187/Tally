namespace TallyNext.Domain.Companies;

/// <summary>Company-scoped role. A user may hold a different role in each company.</summary>
public enum CompanyRole : byte
{
    Owner = 1,
    Accountant = 2,
    DataEntry = 3,
    Viewer = 4,
}

public enum Permission
{
    CompanyView,
    CompanyManage,
    MemberManage,
    PeriodLock,
    MasterView,
    MasterEdit,
    VoucherView,
    VoucherCreate,
    VoucherReverse,
    ReportView,
    AuditView,
    SyncConflictResolve,
}

/// <summary>The single source of truth for what each role may do. Enforced server-side and offline.</summary>
public static class RolePermissions
{
    private static readonly IReadOnlyDictionary<CompanyRole, HashSet<Permission>> Matrix =
        new Dictionary<CompanyRole, HashSet<Permission>>
        {
            [CompanyRole.Owner] = [.. Enum.GetValues<Permission>()],
            [CompanyRole.Accountant] =
            [
                Permission.CompanyView, Permission.MasterView, Permission.MasterEdit,
                Permission.VoucherView, Permission.VoucherCreate, Permission.VoucherReverse,
                Permission.ReportView, Permission.AuditView, Permission.SyncConflictResolve,
            ],
            [CompanyRole.DataEntry] =
            [
                Permission.CompanyView, Permission.MasterView,
                Permission.VoucherView, Permission.VoucherCreate, Permission.ReportView,
            ],
            [CompanyRole.Viewer] =
            [
                Permission.CompanyView, Permission.MasterView, Permission.VoucherView, Permission.ReportView,
            ],
        };

    public static bool Has(CompanyRole role, Permission permission) =>
        Matrix.TryGetValue(role, out var set) && set.Contains(permission);

    public static IReadOnlyCollection<Permission> For(CompanyRole role) =>
        Matrix.TryGetValue(role, out var set) ? set : [];
}
