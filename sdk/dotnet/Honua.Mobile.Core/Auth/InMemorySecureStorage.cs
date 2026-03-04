// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;

namespace Honua.Mobile.Core.Auth;

/// <summary>
/// In-memory implementation of secure storage for testing and development.
/// WARNING: This is NOT secure and should only be used for testing or development.
/// For production, use platform-specific implementations that leverage
/// iOS Keychain, Android Keystore, or Windows Credential Manager.
/// </summary>
public sealed class InMemorySecureStorage : ISecureStorage
{
    private readonly ConcurrentDictionary<string, string> _storage = new();

    /// <inheritdoc />
    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        _storage[key] = value;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _storage.TryGetValue(key, out var value);
        return Task.FromResult(value);
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var removed = _storage.TryRemove(key, out _);
        return Task.FromResult(removed);
    }

    /// <inheritdoc />
    public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var exists = _storage.ContainsKey(key);
        return Task.FromResult(exists);
    }

    /// <inheritdoc />
    public Task RemoveAllAsync(CancellationToken cancellationToken = default)
    {
        _storage.Clear();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the current number of stored items (for testing).
    /// </summary>
    public int Count => _storage.Count;

    /// <summary>
    /// Gets all stored keys (for testing).
    /// </summary>
    public IEnumerable<string> Keys => _storage.Keys;
}