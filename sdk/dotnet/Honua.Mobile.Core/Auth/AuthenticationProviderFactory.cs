// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Mobile.Core.Auth;

/// <summary>
/// Factory for creating appropriate authentication providers based on the environment.
/// </summary>
public static class AuthenticationProviderFactory
{
    /// <summary>
    /// Creates a basic API key authentication provider with in-memory storage.
    /// Suitable for testing and development scenarios.
    /// </summary>
    /// <param name="apiKey">Optional initial API key</param>
    /// <returns>Basic authentication provider</returns>
    public static IMobileAuthenticationProvider CreateBasic(string? apiKey = null)
    {
        return new ApiKeyAuthenticationProvider(apiKey);
    }

    /// <summary>
    /// Creates a secure API key authentication provider using the provided secure storage.
    /// Recommended for production scenarios with platform-specific secure storage.
    /// </summary>
    /// <param name="secureStorage">Platform-specific secure storage implementation</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    /// <param name="storageKey">Optional custom storage key</param>
    /// <returns>Secure authentication provider</returns>
    public static IMobileAuthenticationProvider CreateSecure(
        ISecureStorage secureStorage,
        ILogger<SecureApiKeyAuthenticationProvider>? logger = null,
        string? storageKey = null)
    {
        return new SecureApiKeyAuthenticationProvider(secureStorage, logger, storageKey);
    }

    /// <summary>
    /// Creates an in-memory secure authentication provider for testing.
    /// WARNING: This is NOT secure and should only be used for testing.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics</param>
    /// <returns>In-memory authentication provider (NOT secure)</returns>
    public static IMobileAuthenticationProvider CreateInMemorySecure(
        ILogger<SecureApiKeyAuthenticationProvider>? logger = null)
    {
        var inMemoryStorage = new InMemorySecureStorage();
        return new SecureApiKeyAuthenticationProvider(inMemoryStorage, logger);
    }

    /// <summary>
    /// Creates the recommended authentication provider for the current platform.
    /// In the base implementation, this returns the in-memory provider.
    /// Platform-specific packages should override this to return secure implementations.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics</param>
    /// <returns>Platform-appropriate authentication provider</returns>
    public static IMobileAuthenticationProvider CreateForPlatform(
        ILogger? logger = null)
    {
        // Base implementation uses in-memory storage
        // Platform-specific packages (Honua.Mobile.Maui, etc.) should override this
        return CreateInMemorySecure(logger as ILogger<SecureApiKeyAuthenticationProvider>);
    }
}