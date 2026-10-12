using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using TallyNext.UI.Services;

namespace TallyNext.UI.Components;

/// <summary>Base class for pages: injected services, busy/error state and safe execution of use cases.</summary>
public abstract class AppPage : ComponentBase, IDisposable
{
    private IDisposable? _shortcuts;

    [Inject]
    protected IUseCaseRunner Runner { get; set; } = default!;

    [Inject]
    protected AppState State { get; set; } = default!;

    [Inject]
    protected NavigationManager Nav { get; set; } = default!;

    [Inject]
    protected KeyboardShortcuts Shortcuts { get; set; } = default!;

    [Inject]
    protected ILoggerFactory LoggerFactory { get; set; } = default!;

    protected string? Error { get; set; }

    protected string? Notice { get; set; }

    protected bool Busy { get; set; }

    /// <summary>Pages that need a selected company redirect to the company list when there is none.</summary>
    protected virtual bool RequiresCompany => true;

    protected override void OnInitialized()
    {
        State.Changed += OnStateChanged;
        _shortcuts = Shortcuts.Register(HandleShortcutAsync);
        if (RequiresCompany && !State.HasCompany)
        {
            Nav.NavigateTo("companies");
        }
    }

    /// <summary>Override to handle page-specific shortcuts (return true when handled).</summary>
    protected virtual Task<bool> HandleShortcutAsync(string key) => Task.FromResult(false);

    /// <summary>Runs an action, capturing expected failures as a user-visible message.</summary>
    protected async Task<bool> RunAsync(Func<Task> action)
    {
        Error = null;
        Busy = true;
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            Error = UiErrors.Message(ex, LoggerFactory.CreateLogger(GetType()));
            return false;
        }
        finally
        {
            Busy = false;
        }
    }

    public virtual void Dispose()
    {
        State.Changed -= OnStateChanged;
        _shortcuts?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnStateChanged() => _ = InvokeAsync(StateHasChanged);
}
