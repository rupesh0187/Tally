namespace TallyNext.Desktop.WinUI;

/// <summary>WinUI entry point; hands over to the cross-platform <see cref="MauiProgram"/>.</summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
