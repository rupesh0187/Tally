using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TallyNext.Application.Accounts;
using TallyNext.Application.Audit;
using TallyNext.Application.Common;
using TallyNext.Application.Companies;
using TallyNext.Application.Reports;
using TallyNext.Application.Security;
using TallyNext.Application.Vouchers;

namespace TallyNext.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the use cases. The host must also register <see cref="Abstractions.IAccountingDbContext"/>,
    /// <see cref="Abstractions.IExecutionContext"/> and <see cref="Abstractions.IVoucherSeriesProvider"/>.
    /// </summary>
    public static IServiceCollection AddTallyNextApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IAccessGuard, AccessGuard>();
        services.AddScoped<IAuditTrail, AuditTrail>();
        services.AddScoped<VoucherPostingRules>();
        services.AddScoped<VoucherNumberAllocator>();
        services.AddScoped<CompanyService>();
        services.AddScoped<MembershipService>();
        services.AddScoped<ChartOfAccountsService>();
        services.AddScoped<VoucherService>();
        services.AddScoped<ReportService>();
        services.AddScoped<AuditService>();
        return services;
    }
}
