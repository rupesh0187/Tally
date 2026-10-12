using System.Globalization;
using Microsoft.Extensions.Logging;
using TallyNext.Application.Common;
using TallyNext.Contracts.Companies;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;

namespace TallyNext.UI.Services;

/// <summary>
/// Runs a use case in a fresh dependency-injection scope as the signed-in user. Components never hold a database
/// context: Blazor circuits and the desktop app live far longer than a unit of work.
/// </summary>
public interface IUseCaseRunner
{
    Task<TResult> RunAsync<TService, TResult>(Func<TService, Task<TResult>> action)
        where TService : notnull;

    Task RunAsync<TService>(Func<TService, Task> action)
        where TService : notnull;
}

/// <summary>Capabilities that differ between the desktop and the browser.</summary>
public interface IHostFeatures
{
    /// <summary>True in the desktop application (offline storage and synchronization).</summary>
    bool IsDesktop { get; }

    /// <summary>Adding members needs the server's user directory (online only).</summary>
    bool CanManageMembers { get; }

    string ProductTitle => "TallyNext";

    Task SignOutAsync();
}

/// <summary>Selected company and user for the current session (per browser circuit, or per desktop app).</summary>
public sealed class AppState
{
    public event Action? Changed;

    public string? UserName { get; private set; }

    public CompanySummaryDto? Company { get; private set; }

    public Guid CompanyId => Company?.Id ?? throw new InvalidOperationException("No company is selected.");

    public bool HasCompany => Company is not null;

    public CompanyRole? Role => Company?.Role;

    public string CurrencySymbol => Currencies.Find(Company?.CurrencyCode)?.Symbol ?? "₹";

    public DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>The financial year containing today, clamped to start no earlier than the books.</summary>
    public FinancialYear CurrentYear => Company is null
        ? FinancialYear.Containing(Today, 4)
        : FinancialYear.Containing(Today < Company.BooksBeginningDate ? Company.BooksBeginningDate : Today, Company.FinancialYearStartMonth);

    public DateOnly PeriodStart => Company is null ? CurrentYear.Start : Max(CurrentYear.Start, Company.BooksBeginningDate);

    public bool Can(Permission permission) => Role is { } role && RolePermissions.Has(role, permission);

    public void SetUser(string? userName)
    {
        UserName = userName;
        Changed?.Invoke();
    }

    public void SelectCompany(CompanySummaryDto? company)
    {
        Company = company;
        Changed?.Invoke();
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
}

/// <summary>Synchronization status and conflict handling as shown in the UI. Desktop: real sync; web: server view.</summary>
public interface ISyncPanel
{
    event Action? Changed;

    SyncPanelStatus Status { get; }

    bool CanSyncNow { get; }

    Task SyncNowAsync();

    /// <summary>Recomputes counts after a local save so unsynchronized work is visible immediately.</summary>
    Task RefreshAsync();

    Task<IReadOnlyList<SyncIssue>> IssuesAsync(Guid companyId);

    Task<IReadOnlyList<UnsyncedChange>> UnsyncedAsync(Guid companyId);

    Task ResolveAsync(Guid companyId, Guid issueId, bool keepServerVersion);
}

public enum SyncPanelState
{
    Online,
    Offline,
    Syncing,
    UpToDate,
    Pending,
    NeedsAttention,
    SignInRequired,
    UpdateRequired,
    Error,
}

public sealed record SyncPanelStatus(SyncPanelState State, int PendingCount, int AttentionCount, DateTime? LastSuccessUtc, string? Message)
{
    public string Label => State switch
    {
        SyncPanelState.Online => "Online",
        SyncPanelState.Offline => PendingCount > 0 ? $"Offline · {PendingCount} not uploaded" : "Offline",
        SyncPanelState.Syncing => "Synchronizing…",
        SyncPanelState.UpToDate => "Synchronized",
        SyncPanelState.Pending => $"{PendingCount} change(s) not uploaded yet",
        SyncPanelState.NeedsAttention => $"{AttentionCount} item(s) need attention",
        SyncPanelState.SignInRequired => "Sign in online to synchronize",
        SyncPanelState.UpdateRequired => "Application update required",
        _ => "Synchronization error",
    };

    public string Tone => State switch
    {
        SyncPanelState.UpToDate or SyncPanelState.Online => "ok",
        SyncPanelState.Syncing => "busy",
        SyncPanelState.Pending or SyncPanelState.Offline => "warn",
        _ => "bad",
    };
}

public sealed record SyncIssue(Guid Id, string EntityType, Guid EntityId, string Kind, string? Code, string? Message, string LocalJson, string? ServerJson, DateTime DetectedAtUtc);

public sealed record UnsyncedChange(long Sequence, string EntityType, Guid EntityId, string Status, int Attempts, string? LastErrorCode, DateTime ModifiedAtUtc);

/// <summary>Display formatting (Indian digit grouping: 12,34,567.89).</summary>
public static class Fmt
{
    private static readonly CultureInfo Indian = CreateIndian();

    public static string Amount(decimal value) => value == 0 ? "" : value.ToString("N2", Indian);

    public static string AmountOrZero(decimal value) => value.ToString("N2", Indian);

    public static string Balance(decimal debit, decimal credit) =>
        debit > 0 ? $"{debit.ToString("N2", Indian)} Dr" : credit > 0 ? $"{credit.ToString("N2", Indian)} Cr" : "—";

    public static string Date(DateOnly date) => date.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture);

    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture);

    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Parses user-typed amounts ("1,23,456.50", "1234.5"). Returns null when not a number.</summary>
    public static decimal? ParseAmount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cleaned = text.Replace(",", string.Empty, StringComparison.Ordinal).Replace("₹", string.Empty, StringComparison.Ordinal).Trim();
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static CultureInfo CreateIndian()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberGroupSizes = [3, 2];
        culture.NumberFormat.NumberGroupSeparator = ",";
        culture.NumberFormat.NumberDecimalSeparator = ".";
        return culture;
    }
}

/// <summary>Turns exceptions into messages that are safe and useful to show to users.</summary>
public static class UiErrors
{
    public static string Message(Exception exception, ILogger? logger = null) => exception switch
    {
        AppException app => app.Message,
        DomainException domain => domain.Message,
        _ => Unexpected(exception, logger),
    };

    private static string Unexpected(Exception exception, ILogger? logger)
    {
        logger?.LogError(exception, "Unexpected UI error");
        return "Something went wrong. The details were written to the log; please try again.";
    }
}
