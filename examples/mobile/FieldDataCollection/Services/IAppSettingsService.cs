namespace FieldDataCollection.Services;

/// <summary>
/// Service for managing application settings and configuration.
/// Provides access to server configuration, authentication settings, and user preferences.
/// </summary>
public interface IAppSettingsService
{
    /// <summary>
    /// Gets the configured Honua server URL.
    /// </summary>
    /// <returns>Server URL or null if not configured.</returns>
    string? GetServerUrl();

    /// <summary>
    /// Sets the Honua server URL.
    /// </summary>
    /// <param name="serverUrl">The server URL to configure.</param>
    Task SetServerUrlAsync(string serverUrl);

    /// <summary>
    /// Gets the current API key for authentication.
    /// </summary>
    /// <returns>API key or null if not configured.</returns>
    Task<string?> GetApiKeyAsync();

    /// <summary>
    /// Sets the API key for authentication.
    /// </summary>
    /// <param name="apiKey">The API key to store securely.</param>
    Task SetApiKeyAsync(string apiKey);

    /// <summary>
    /// Clears stored authentication credentials.
    /// </summary>
    Task ClearCredentialsAsync();

    /// <summary>
    /// Gets whether automatic sync is enabled.
    /// </summary>
    bool IsAutoSyncEnabled { get; }

    /// <summary>
    /// Sets whether automatic sync should be enabled.
    /// </summary>
    /// <param name="enabled">True to enable automatic sync.</param>
    Task SetAutoSyncEnabledAsync(bool enabled);

    /// <summary>
    /// Gets the sync interval in minutes.
    /// </summary>
    int SyncIntervalMinutes { get; }

    /// <summary>
    /// Sets the sync interval in minutes.
    /// </summary>
    /// <param name="minutes">Sync interval in minutes.</param>
    Task SetSyncIntervalAsync(int minutes);
}