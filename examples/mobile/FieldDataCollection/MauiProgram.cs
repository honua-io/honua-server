// using CommunityToolkit.Maui;
using FieldDataCollection.Services;
using FieldDataCollection.ViewModels;
using FieldDataCollection.Views;
using Honua.Mobile.Core.Auth;
using Honua.Mobile.Core.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FieldDataCollection;

/// <summary>
/// Main application configuration and dependency injection setup.
/// </summary>
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            // .UseMauiCommunityToolkit()  // Not available for .NET 10 yet
            .UseMauiMaps()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Configure logging
        builder.Services.AddLogging(configure =>
        {
            configure.AddDebug();
            configure.SetMinimumLevel(LogLevel.Information);
        });

        // Register SDK services
        RegisterSDKServices(builder.Services);

        // Register app services
        RegisterAppServices(builder.Services);

        // Register views and view models
        RegisterViewsAndViewModels(builder.Services);

        return builder.Build();
    }

    private static void RegisterSDKServices(IServiceCollection services)
    {
        // Authentication
        services.AddSingleton<IMobileAuthenticationProvider>(sp =>
            AuthenticationProviderFactory.CreateForPlatform());

        // gRPC Client - will be configured with server URL from settings
        services.AddSingleton<HonuaFeatureClient>(sp =>
        {
            var auth = sp.GetRequiredService<IMobileAuthenticationProvider>();
            var settings = sp.GetRequiredService<IAppSettingsService>();
            var serverUrl = settings.GetServerUrl() ?? "https://api.honua.com";
            return new HonuaFeatureClient(serverUrl, auth);
        });

        // Offline storage with GeoPackage
        services.AddLocalStorage(GetConfiguration());
        services.Configure<HonuaMobileClientOptions>(GetConfiguration().GetSection(HonuaMobileClientOptions.SectionName));
        services.AddSingleton<HonuaMobileClient>();
    }

    private static void RegisterAppServices(IServiceCollection services)
    {
        services.AddSingleton<IAppSettingsService, AppSettingsService>();
        services.AddSingleton<ILocationService, LocationService>();
        services.AddSingleton<ISyncService, SyncService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddTransient<INavigationService, NavigationService>();
    }

    private static void RegisterViewsAndViewModels(IServiceCollection services)
    {
        // ViewModels
        services.AddTransient<MapViewModel>();
        services.AddTransient<RecordDetailViewModel>();
        services.AddTransient<SyncCenterViewModel>();
        services.AddTransient<SettingsViewModel>();

        // Views
        services.AddTransient<MapPage>();
        services.AddTransient<RecordDetailPage>();
        services.AddTransient<SyncCenterPage>();
        services.AddTransient<SettingsPage>();
    }

    private static IConfiguration GetConfiguration()
    {
        // Create basic configuration for mobile app
        var configBuilder = new ConfigurationBuilder();

        // Add appsettings.json if available
        var assembly = typeof(MauiProgram).Assembly;
        using var stream = assembly.GetManifestResourceStream("FieldDataCollection.appsettings.json");
        if (stream != null)
        {
            configBuilder.AddJsonStream(stream);
        }

        // Add platform-specific settings
#if DEBUG
        configBuilder.AddInMemoryCollection(new Dictionary<string, string>
        {
            ["HonuaMobileClient:ServerUrl"] = "https://localhost:5001",
            ["HonuaMobileClient:AcceptInvalidCertificates"] = "true",
            ["LocalStorage:EnableDetailedLogging"] = "true"
        });
#endif

        return configBuilder.Build();
    }
}