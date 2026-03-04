using FieldDataCollection.Services;
using Honua.Mobile.Core.Auth;
using Honua.Mobile.Core.Client;
using System.Windows.Input;

namespace FieldDataCollection.ViewModels;

/// <summary>
/// View model for the Settings screen.
/// Manages application configuration, authentication, and diagnostics.
/// </summary>
public class SettingsViewModel : BaseViewModel
{
    private readonly IAppSettingsService _settings;
    private readonly IMobileAuthenticationProvider _auth;
    private readonly HonuaFeatureClient _client;
    private readonly IDialogService _dialogService;
    private readonly ILocationService _locationService;
    private readonly ISyncService _syncService;

    private string _serverUrl = string.Empty;
    private string _apiKey = string.Empty;
    private bool _hasCredentials;
    private bool _autoSyncEnabled;
    private int _syncIntervalMinutes;
    private bool _locationEnabled;
    private string _appVersion = string.Empty;
    private string _diagnosticsInfo = string.Empty;

    public SettingsViewModel(
        IAppSettingsService settings,
        IMobileAuthenticationProvider auth,
        HonuaFeatureClient client,
        IDialogService dialogService,
        ILocationService locationService,
        ISyncService syncService)
    {
        _settings = settings;
        _auth = auth;
        _client = client;
        _dialogService = dialogService;
        _locationService = locationService;
        _syncService = syncService;

        Title = "Settings";

        // Initialize commands
        SaveSettingsCommand = new Command(async () => await SaveSettingsAsync());
        TestConnectionCommand = new Command(async () => await TestConnectionAsync());
        ClearCredentialsCommand = new Command(async () => await ClearCredentialsAsync());
        RequestLocationPermissionCommand = new Command(async () => await RequestLocationPermissionAsync());
        ViewDiagnosticsCommand = new Command(async () => await ViewDiagnosticsAsync());
        ExportDiagnosticsCommand = new Command(async () => await ExportDiagnosticsAsync());

        // Initialize values
        _ = Task.Run(LoadSettingsAsync);
    }

    #region Properties

    public string ServerUrl
    {
        get => _serverUrl;
        set => SetProperty(ref _serverUrl, value);
    }

    public string ApiKey
    {
        get => _apiKey;
        set => SetProperty(ref _apiKey, value);
    }

    public bool HasCredentials
    {
        get => _hasCredentials;
        set => SetProperty(ref _hasCredentials, value);
    }

    public bool AutoSyncEnabled
    {
        get => _autoSyncEnabled;
        set => SetProperty(ref _autoSyncEnabled, value);
    }

    public int SyncIntervalMinutes
    {
        get => _syncIntervalMinutes;
        set => SetProperty(ref _syncIntervalMinutes, value);
    }

    public bool LocationEnabled
    {
        get => _locationEnabled;
        set => SetProperty(ref _locationEnabled, value);
    }

    public string AppVersion
    {
        get => _appVersion;
        set => SetProperty(ref _appVersion, value);
    }

    public string DiagnosticsInfo
    {
        get => _diagnosticsInfo;
        set => SetProperty(ref _diagnosticsInfo, value);
    }

    #endregion

    #region Commands

    public ICommand SaveSettingsCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand ClearCredentialsCommand { get; }
    public ICommand RequestLocationPermissionCommand { get; }
    public ICommand ViewDiagnosticsCommand { get; }
    public ICommand ExportDiagnosticsCommand { get; }

    #endregion

    #region Private Methods

    private async Task LoadSettingsAsync()
    {
        // Load server configuration
        ServerUrl = _settings.GetServerUrl() ?? "https://api.honua.com";

        // Load authentication
        var apiKey = await _settings.GetApiKeyAsync();
        ApiKey = string.IsNullOrEmpty(apiKey) ? "" : "****" + apiKey.Substring(Math.Max(0, apiKey.Length - 4));
        HasCredentials = await _auth.HasCredentialsAsync();

        // Load sync settings
        AutoSyncEnabled = _settings.IsAutoSyncEnabled;
        SyncIntervalMinutes = _settings.SyncIntervalMinutes;

        // Check location permission
        LocationEnabled = await _locationService.IsLocationAvailableAsync();

        // Load app info
        AppVersion = AppInfo.Current.VersionString;

        // Generate diagnostics
        await GenerateDiagnosticsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        await ExecuteAsync(async () =>
        {
            // Validate server URL
            if (string.IsNullOrWhiteSpace(ServerUrl))
            {
                await _dialogService.DisplayAlertAsync("Validation Error", "Server URL is required");
                return;
            }

            if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                await _dialogService.DisplayAlertAsync("Validation Error", "Server URL must be a valid HTTP or HTTPS URL");
                return;
            }

            // Save settings
            await _settings.SetServerUrlAsync(ServerUrl);
            await _settings.SetAutoSyncEnabledAsync(AutoSyncEnabled);

            if (SyncIntervalMinutes >= 1)
            {
                await _settings.SetSyncIntervalAsync(SyncIntervalMinutes);
            }

            await _dialogService.ShowToastAsync("Settings saved successfully");

        }, OnError);
    }

    private async Task TestConnectionAsync()
    {
        await ExecuteAsync(async () =>
        {
            try
            {
                // Test basic connectivity with a simple query
                var result = await _client.CountAsync("test-service", 0, new());

                await _dialogService.DisplayAlertAsync(
                    "Connection Test",
                    "✅ Successfully connected to server!\n\n" +
                    $"Server: {ServerUrl}\n" +
                    "Authentication: Valid");
            }
            catch (Exception ex)
            {
                await _dialogService.DisplayAlertAsync(
                    "Connection Test Failed",
                    $"❌ Failed to connect to server\n\n" +
                    $"Server: {ServerUrl}\n" +
                    $"Error: {ex.Message}");
            }

        }, OnError);
    }

    private async Task ClearCredentialsAsync()
    {
        var confirmed = await _dialogService.DisplayConfirmAsync(
            "Clear Credentials",
            "Are you sure you want to clear all stored authentication credentials?",
            "Clear",
            "Cancel");

        if (!confirmed) return;

        await ExecuteAsync(async () =>
        {
            await _settings.ClearCredentialsAsync();
            await _auth.ClearCredentialsAsync();

            ApiKey = "";
            HasCredentials = false;

            await _dialogService.ShowToastAsync("Credentials cleared");

        }, OnError);
    }

    private async Task RequestLocationPermissionAsync()
    {
        await ExecuteAsync(async () =>
        {
            var granted = await _locationService.RequestLocationPermissionAsync();
            LocationEnabled = granted;

            var message = granted
                ? "Location permission granted"
                : "Location permission denied";

            await _dialogService.ShowToastAsync(message);

        }, OnError);
    }

    private async Task ViewDiagnosticsAsync()
    {
        await GenerateDiagnosticsAsync();
        await _dialogService.DisplayAlertAsync("System Diagnostics", DiagnosticsInfo);
    }

    private async Task ExportDiagnosticsAsync()
    {
        await GenerateDiagnosticsAsync();

        // For now, just copy to clipboard
        // Future: Save to file or share
        await _dialogService.DisplayAlertAsync(
            "Diagnostics Export",
            "Diagnostics information has been prepared. In a future version, this will be exported to a file or shared directly.\n\n" +
            "Current diagnostics:\n" + DiagnosticsInfo);
    }

    private async Task GenerateDiagnosticsAsync()
    {
        var diagnostics = new List<string>
        {
            $"App Version: {AppInfo.Current.VersionString}",
            $"Platform: {DeviceInfo.Current.Platform}",
            $"Device Model: {DeviceInfo.Current.Model}",
            $"OS Version: {DeviceInfo.Current.VersionString}",
            $"",
            $"Server URL: {ServerUrl}",
            $"Has Credentials: {HasCredentials}",
            $"Auto Sync: {(AutoSyncEnabled ? "Enabled" : "Disabled")}",
            $"Sync Interval: {SyncIntervalMinutes} minutes",
            $"",
            $"Location Enabled: {LocationEnabled}",
            $"Network Access: {Connectivity.Current.NetworkAccess}",
            $"Connection Profiles: {string.Join(", ", Connectivity.Current.ConnectionProfiles)}",
            $"",
            $"Sync Status: {_syncService.CurrentStatus}",
            $"Online Status: {(_syncService.IsOnline ? "Online" : "Offline")}",
            $"Last Sync: {(_syncService.LastSyncTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Never")}",
            $"Pending Changes: {_syncService.PendingChangesCount}"
        };

        // Add location accuracy if available
        try
        {
            var accuracy = await _locationService.GetLocationAccuracyAsync();
            if (accuracy.HasValue)
            {
                diagnostics.Add($"Location Accuracy: {accuracy.Value:F1}m");
            }
        }
        catch (Exception)
        {
            diagnostics.Add("Location Accuracy: Unknown");
        }

        DiagnosticsInfo = string.Join("\n", diagnostics);
    }

    private async void OnError(Exception ex)
    {
        await _dialogService.DisplayAlertAsync("Error", ex.Message);
    }

    #endregion
}