namespace TallyNext.UI.Services;

/// <summary>
/// Dispatches global keyboard shortcuts. The most recently registered handler (usually the active page) gets the
/// first chance to handle a key; unhandled keys fall back to the layout's navigation shortcuts.
/// </summary>
public sealed class KeyboardShortcuts
{
    private readonly List<Func<string, Task<bool>>> _handlers = [];
    private readonly Lock _gate = new();

    public static IReadOnlyList<(string Keys, string Action)> Help { get; } =
    [
        ("F4", "Contra voucher"),
        ("F5", "Payment voucher"),
        ("F6", "Receipt voucher"),
        ("F7", "Journal voucher"),
        ("F8", "Sales voucher"),
        ("F9", "Purchase voucher"),
        ("Alt+F6", "Credit note"),
        ("Alt+F5", "Debit note"),
        ("Alt+D", "Day Book"),
        ("Alt+L", "Ledgers"),
        ("Alt+T", "Trial Balance"),
        ("Alt+P", "Profit & Loss"),
        ("Alt+B", "Balance Sheet"),
        ("Alt+V", "Voucher register"),
        ("Alt+I", "Stock items"),
        ("Alt+K", "Stock Summary"),
        ("Ctrl+H", "Switch between accounting voucher and item invoice"),
        ("Alt+S", "Synchronization"),
        ("Alt+C", "Select company"),
        ("Ctrl+S / Ctrl+Enter", "Save the voucher or form"),
        ("Enter", "Next field (in voucher entry)"),
        ("Esc", "Close list / go back"),
        ("F1", "Keyboard help"),
    ];

    public IDisposable Register(Func<string, Task<bool>> handler)
    {
        lock (_gate)
        {
            _handlers.Add(handler);
        }

        return new Registration(this, handler);
    }

    public async Task<bool> DispatchAsync(string key)
    {
        Func<string, Task<bool>>[] handlers;
        lock (_gate)
        {
            handlers = [.. _handlers];
        }

        for (var i = handlers.Length - 1; i >= 0; i--)
        {
            if (await handlers[i](key))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class Registration(KeyboardShortcuts owner, Func<string, Task<bool>> handler) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._handlers.Remove(handler);
            }
        }
    }
}

/// <summary>Desktop sign-in, implemented by the desktop host (the browser app signs in with its own page).</summary>
public interface IDesktopSignIn
{
    event Action? Changed;

    bool IsSignedIn { get; }

    string? DefaultServerUrl { get; }

    Task<DesktopSignInResult> SignInAsync(string email, string password, string? serverUrl);
}

public sealed record DesktopSignInResult(bool Succeeded, bool Offline, string? Error);

/// <summary>Maps URL slugs to voucher base types (/vouchers/new/payment).</summary>
public static class VoucherSlugs
{
    public static string Of(TallyNext.Domain.Vouchers.VoucherBaseType type) => type switch
    {
        TallyNext.Domain.Vouchers.VoucherBaseType.CreditNote => "credit-note",
        TallyNext.Domain.Vouchers.VoucherBaseType.DebitNote => "debit-note",
        _ => type.ToString().ToLowerInvariant(),
    };

    public static TallyNext.Domain.Vouchers.VoucherBaseType? Parse(string? slug) => slug?.ToLowerInvariant() switch
    {
        "contra" => TallyNext.Domain.Vouchers.VoucherBaseType.Contra,
        "payment" => TallyNext.Domain.Vouchers.VoucherBaseType.Payment,
        "receipt" => TallyNext.Domain.Vouchers.VoucherBaseType.Receipt,
        "journal" => TallyNext.Domain.Vouchers.VoucherBaseType.Journal,
        "sales" => TallyNext.Domain.Vouchers.VoucherBaseType.Sales,
        "purchase" => TallyNext.Domain.Vouchers.VoucherBaseType.Purchase,
        "credit-note" => TallyNext.Domain.Vouchers.VoucherBaseType.CreditNote,
        "debit-note" => TallyNext.Domain.Vouchers.VoucherBaseType.DebitNote,
        _ => null,
    };

    public static string ShortcutOf(TallyNext.Domain.Vouchers.VoucherBaseType type) => type switch
    {
        TallyNext.Domain.Vouchers.VoucherBaseType.Contra => "F4",
        TallyNext.Domain.Vouchers.VoucherBaseType.Payment => "F5",
        TallyNext.Domain.Vouchers.VoucherBaseType.Receipt => "F6",
        TallyNext.Domain.Vouchers.VoucherBaseType.Journal => "F7",
        TallyNext.Domain.Vouchers.VoucherBaseType.Sales => "F8",
        TallyNext.Domain.Vouchers.VoucherBaseType.Purchase => "F9",
        TallyNext.Domain.Vouchers.VoucherBaseType.CreditNote => "Alt+F6",
        _ => "Alt+F5",
    };
}
