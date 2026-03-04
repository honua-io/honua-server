// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using Grpc.Core;

namespace Honua.Mobile.Core.Auth;

/// <summary>
/// Basic API key authentication provider for mobile applications.
/// Uses in-memory storage by default. For production use, inherit and override
/// storage methods to use platform-specific secure storage (iOS Keychain, Android Keystore).
/// </summary>
public class ApiKeyAuthenticationProvider : IMobileAuthenticationProvider
{
    private const string ApiKeyHeaderName = "X-API-Key";
    private string? _apiKey;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance with an optional API key.
    /// </summary>
    /// <param name="apiKey">Optional API key to set immediately</param>
    public ApiKeyAuthenticationProvider(string? apiKey = null)
    {
        _apiKey = apiKey;
    }

    /// <inheritdoc />
    public virtual async Task<Metadata> GetAuthHeadersAsync(CancellationToken cancellationToken = default)
    {
        var apiKey = await GetStoredApiKeyAsync().ConfigureAwait(false);

        var metadata = new Metadata();

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            metadata.Add(ApiKeyHeaderName, apiKey);
        }

        return metadata;
    }

    /// <inheritdoc />
    public virtual async Task SetApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        await StoreApiKeyAsync(apiKey).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task ClearCredentialsAsync(CancellationToken cancellationToken = default)
    {
        await ClearStoredApiKeyAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<bool> HasCredentialsAsync(CancellationToken cancellationToken = default)
    {
        var apiKey = await GetStoredApiKeyAsync().ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(apiKey);
    }

    /// <summary>
    /// Gets the stored API key. Override this method to implement platform-specific secure storage.
    /// </summary>
    /// <returns>The stored API key, or null if none is stored</returns>
    protected virtual Task<string?> GetStoredApiKeyAsync()
    {
        lock (_lock)
        {
            return Task.FromResult(_apiKey);
        }
    }

    /// <summary>
    /// Stores the API key securely. Override this method to implement platform-specific secure storage.
    /// </summary>
    /// <param name="apiKey">The API key to store</param>
    /// <returns>A task representing the storage operation</returns>
    protected virtual Task StoreApiKeyAsync(string apiKey)
    {
        lock (_lock)
        {
            _apiKey = apiKey;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Clears the stored API key. Override this method to implement platform-specific secure storage.
    /// </summary>
    /// <returns>A task representing the clear operation</returns>
    protected virtual Task ClearStoredApiKeyAsync()
    {
        lock (_lock)
        {
            _apiKey = null;
            return Task.CompletedTask;
        }
    }
}