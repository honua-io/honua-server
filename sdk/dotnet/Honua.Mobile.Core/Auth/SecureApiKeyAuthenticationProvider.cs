// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Honua.Mobile.Core.Auth;

/// <summary>
/// API key authentication provider that uses platform-specific secure storage.
/// Provides secure storage and retrieval of API keys using iOS Keychain,
/// Android Keystore, or Windows Credential Manager.
/// </summary>
public sealed class SecureApiKeyAuthenticationProvider : IMobileAuthenticationProvider
{
    private const string ApiKeyHeaderName = "X-API-Key";
    private const string DefaultApiKeyStorageKey = "honua_api_key";

    private readonly ISecureStorage _secureStorage;
    private readonly ILogger<SecureApiKeyAuthenticationProvider> _logger;
    private readonly string _storageKey;

    /// <summary>
    /// Initializes a new instance of the SecureApiKeyAuthenticationProvider.
    /// </summary>
    /// <param name="secureStorage">Platform-specific secure storage implementation</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    /// <param name="storageKey">Optional custom storage key (defaults to "honua_api_key")</param>
    public SecureApiKeyAuthenticationProvider(
        ISecureStorage secureStorage,
        ILogger<SecureApiKeyAuthenticationProvider>? logger = null,
        string? storageKey = null)
    {
        _secureStorage = secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SecureApiKeyAuthenticationProvider>.Instance;
        _storageKey = storageKey ?? DefaultApiKeyStorageKey;
    }

    /// <inheritdoc />
    public async Task<Metadata> GetAuthHeadersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var apiKey = await _secureStorage.GetAsync(_storageKey, cancellationToken).ConfigureAwait(false);

            var metadata = new Metadata();

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                metadata.Add(ApiKeyHeaderName, apiKey);
                _logger.LogDebug("Retrieved API key from secure storage for authentication");
            }
            else
            {
                _logger.LogWarning("No API key found in secure storage");
            }

            return metadata;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve API key from secure storage");
            throw new AuthenticationException("Failed to retrieve authentication credentials from secure storage", ex);
        }
    }

    /// <inheritdoc />
    public async Task SetApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        try
        {
            await _secureStorage.SetAsync(_storageKey, apiKey, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("API key stored securely");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store API key in secure storage");
            throw new AuthenticationException("Failed to store authentication credentials in secure storage", ex);
        }
    }

    /// <inheritdoc />
    public async Task ClearCredentialsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var removed = await _secureStorage.RemoveAsync(_storageKey, cancellationToken).ConfigureAwait(false);

            if (removed)
            {
                _logger.LogDebug("API key removed from secure storage");
            }
            else
            {
                _logger.LogDebug("No API key was found to remove from secure storage");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear credentials from secure storage");
            throw new AuthenticationException("Failed to clear authentication credentials from secure storage", ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> HasCredentialsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _secureStorage.ContainsKeyAsync(_storageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check for credentials in secure storage");
            return false;
        }
    }

    /// <summary>
    /// Gets the stored API key directly (for testing or diagnostics).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The stored API key, or null if none is stored</returns>
    public async Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _secureStorage.GetAsync(_storageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve API key from secure storage");
            return null;
        }
    }
}

/// <summary>
/// Exception thrown when authentication operations fail.
/// </summary>
public sealed class AuthenticationException : Exception
{
    public AuthenticationException(string message) : base(message) { }
    public AuthenticationException(string message, Exception innerException) : base(message, innerException) { }
}