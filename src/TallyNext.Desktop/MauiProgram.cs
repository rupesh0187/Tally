using Microsoft.Extensions.Logging;
using Serilog;
using TallyNext.Desktop.Core;
using TallyNext.Infrastructure.Local;

namespace TallyNext.Desktop;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();

        var dataFolder = Path.GetDirectoryName(LocalDatabaseOptions.DefaultDatabasePath())!;
        var fileLogger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(dataFolder, "logs", "desktop-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();
        builder.Logging.AddSerilog(fileLogger, dispose: true);

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        // Local SQLite database, offline sign-in, background sync and the shared screens.
        builder.Services.AddTallyNextDesktop();

        var app = builder.Build();

        // Apply database migrations and load the device identity before the first window opens.
        app.Services.GetRequiredService<DesktopStartup>().InitializeAsync().GetAwaiter().GetResult();
        return app;
    }
}
