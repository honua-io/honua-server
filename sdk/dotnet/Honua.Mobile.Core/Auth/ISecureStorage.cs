// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

namespace Honua.Mobile.Core.Auth;

/// <summary>
/// Provides secure storage for sensitive data like API keys and tokens.
/// Platform-specific implementations should use iOS Keychain, Android Keystore,
/// Windows Credential Manager, etc.
/// </summary>
public interface ISecureStorage
{
    /// <summary>
    /// Stores a value securely with the given key.
    /// </summary>
    /// <param name="key">The key to store the value under</param>
    /// <param name="value">The value to store securely</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task representing the storage operation</returns>
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a securely stored value by key.
    /// </summary>
    /// <param name="key">The key to retrieve the value for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The stored value, or null if not found</returns>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a securely stored value by key.
    /// </summary>
    /// <param name="key">The key to remove</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the value was found and removed, false otherwise</returns>
    Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a value exists for the given key.
    /// </summary>
    /// <param name="key">The key to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if a value exists for the key, false otherwise</returns>
    Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all securely stored values.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task representing the clear operation</returns>
    Task RemoveAllAsync(CancellationToken cancellationToken = default);
}