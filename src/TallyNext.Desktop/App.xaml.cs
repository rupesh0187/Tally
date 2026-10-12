using TallyNext.Infrastructure.Local.Sync;

namespace TallyNext.Desktop;

public partial class App : Microsoft.Maui.Controls.Application
{
    private readonly SyncScheduler _scheduler;

    public App(SyncScheduler scheduler)
    {
        InitializeComponent();
        _scheduler = scheduler;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage())
        {
            Title = "TallyNext",
            MinimumWidth = 960,
            MinimumHeight = 640,
        };

        // Stop background synchronization cleanly; every sync step is transactional, so this is always safe.
        window.Destroying += (_, _) => _scheduler.StopAsync().GetAwaiter().GetResult();
        return window;
    }
}
