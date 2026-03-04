namespace FieldDataCollection.Services;

/// <summary>
/// Implementation of IAppSettingsService using Microsoft.Maui.Storage preferences
/// and secure storage for sensitive data.
/// </summary>
public class AppSettingsService : IAppSettingsService
{
    private const string ServerUrlKey = "server_url";
    private const string ApiKeyKey = "api_key";
    private const string AutoSyncEnabledKey = "auto_sync_enabled";
    private const string SyncIntervalKey = "sync_interval_minutes";

    public string? GetServerUrl()
    {
        return Preferences.Get(ServerUrlKey, (string?)null);
    }

    public Task SetServerUrlAsync(string serverUrl)
    {
        Preferences.Set(ServerUrlKey, serverUrl);
        return Task.CompletedTask;
    }

    public async Task<string?> GetApiKeyAsync()
    {
        try
        {
            return await SecureStorage.GetAsync(ApiKeyKey);
        }
        catch (Exception)
        {
            // Secure storage may fail on some platforms/configurations
            return null;
        }
    }

    public async Task SetApiKeyAsync(string apiKey)
    {
        try
        {
            await SecureStorage.SetAsync(ApiKeyKey, apiKey);
        }
        catch (Exception)
        {
            // Fallback to preferences if secure storage fails
            Preferences.Set(ApiKeyKey, apiKey);
        }
    }

    public async Task ClearCredentialsAsync()
    {
        try
        {
            SecureStorage.Remove(ApiKeyKey);
        }
        catch (Exception)
        {
            // Also clear from preferences
            Preferences.Remove(ApiKeyKey);
        }

        await Task.CompletedTask;
    }

    public bool IsAutoSyncEnabled => Preferences.Get(AutoSyncEnabledKey, true);

    public Task SetAutoSyncEnabledAsync(bool enabled)
    {
        Preferences.Set(AutoSyncEnabledKey, enabled);
        return Task.CompletedTask;
    }

    public int SyncIntervalMinutes => Preferences.Get(SyncIntervalKey, 15); // Default 15 minutes

    public Task SetSyncIntervalAsync(int minutes)
    {
        if (minutes < 1)
            throw new ArgumentOutOfRangeException(nameof(minutes), "Sync interval must be at least 1 minute");

        Preferences.Set(SyncIntervalKey, minutes);
        return Task.CompletedTask;
    }
}