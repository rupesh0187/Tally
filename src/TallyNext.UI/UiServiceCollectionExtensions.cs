using Microsoft.Extensions.DependencyInjection;
using TallyNext.UI.Services;

namespace TallyNext.UI;

public static class UiServiceCollectionExtensions
{
    /// <summary>
    /// Registers session-level UI state. <paramref name="lifetime"/> is Scoped for Blazor Server (one per circuit) and
    /// Singleton for the desktop (one per application).
    /// </summary>
    public static IServiceCollection AddTallyNextUi(this IServiceCollection services, ServiceLifetime lifetime)
    {
        services.Add(new ServiceDescriptor(typeof(AppState), typeof(AppState), lifetime));
        services.Add(new ServiceDescriptor(typeof(LedgerCache), typeof(LedgerCache), lifetime));
        services.Add(new ServiceDescriptor(typeof(KeyboardShortcuts), typeof(KeyboardShortcuts), lifetime));
        return services;
    }
}

/// <summary>Marker for locating the UI assembly (router additional assemblies, static assets).</summary>
public sealed class UiAssembly;
