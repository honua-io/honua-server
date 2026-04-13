// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Configuration;

/// <summary>
/// Centralized secret provider that integrates with multiple secret management systems.
/// Provides caching, fallback, and audit logging for secret access.
/// </summary>
internal sealed class SecretProvider : ISecretProvider, IDisposable
{
    private readonly IConnectionSecretResolver _secretResolver;
    private readonly SecretProviderOptions _options;
    private readonly ILogger<SecretProvider> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private readonly Timer? _cacheCleanupTimer;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the SecretProvider.
    /// </summary>
    public SecretProvider(
        IConnectionSecretResolver secretResolver,
        IOptions<SecretProviderOptions> options,
        ILogger<SecretProvider> logger)
    {
        _secretResolver = secretResolver ?? throw new ArgumentNullException(nameof(secretResolver));
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (_options.EnableCaching)
        {
            // Run cache cleanup every hour
            _cacheCleanupTimer = new Timer(CleanupExpiredEntries, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        }
    }

    /// <summary>
    /// Retrieves a secret value using the specified secret reference.
    /// </summary>
    public async Task<string?> GetSecretAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);

        if (_options.LogSecretAccess)
        {
            _logger.LogDebug("Attempting to retrieve secret with reference: {SecretRef}", MaskSecretReference(secretRef));
        }

        // Check cache first
        if (_options.EnableCaching && TryGetFromCache(secretRef, out var cachedValue))
        {
            _logger.LogDebug("Secret retrieved from cache: {SecretRef}", MaskSecretReference(secretRef));
            return cachedValue;
        }

        try
        {
            using var timeoutCts = new CancellationTokenSource(_options.OperationTimeout);
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var value = await _secretResolver.ResolveConnectionStringAsync(secretRef, combinedCts.Token)
                .ConfigureAwait(false);

            if (_options.EnableCaching && !string.IsNullOrEmpty(value))
            {
                await CacheSecretAsync(secretRef, value).ConfigureAwait(false);
            }

            _logger.LogDebug("Secret successfully retrieved: {SecretRef}", MaskSecretReference(secretRef));
            return value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to retrieve secret: {SecretRef}", MaskSecretReference(secretRef));
            throw new SecretNotFoundException(secretRef, $"Failed to retrieve secret: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Retrieves a secret value or returns the default value if not found.
    /// </summary>
    public async Task<string?> GetSecretOrDefaultAsync(string secretRef, string? defaultValue = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetSecretAsync(secretRef, cancellationToken).ConfigureAwait(false);
        }
        catch (SecretNotFoundException)
        {
            _logger.LogDebug("Secret not found, using default value: {SecretRef}", MaskSecretReference(secretRef));
            return defaultValue;
        }
    }

    /// <summary>
    /// Tests whether a secret reference can be resolved.
    /// </summary>
    public async Task<bool> CanResolveSecretAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            return false;
        }

        try
        {
            using var timeoutCts = new CancellationTokenSource(_options.OperationTimeout);
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            return await _secretResolver.CanResolveSecretAsync(secretRef, combinedCts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to test secret resolution: {SecretRef}", MaskSecretReference(secretRef));
            return false;
        }
    }

    /// <summary>
    /// Gets the supported secret provider types.
    /// </summary>
    public string[] GetSupportedProviders() => _secretResolver.GetSupportedProviders();

    /// <summary>
    /// Determines if a value is a secret reference.
    /// </summary>
    public bool IsSecretReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var colonIndex = value.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex <= 0)
        {
            return false;
        }

        var prefix = value[..colonIndex];
        return GetSupportedProviders().Contains(prefix, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tries to get a value from the cache.
    /// </summary>
    private bool TryGetFromCache(string secretRef, out string? value)
    {
        value = null;

        if (!_cache.TryGetValue(secretRef, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            // Remove expired entry
            _cache.TryRemove(secretRef, out _);
            return false;
        }

        value = entry.Value;
        return true;
    }

    /// <summary>
    /// Caches a secret value with expiration.
    /// </summary>
    private async Task CacheSecretAsync(string secretRef, string value)
    {
        // Enforce cache size limit
        if (_cache.Count >= _options.MaxCacheSize)
        {
            await _cacheLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_cache.Count >= _options.MaxCacheSize)
                {
                    // Remove oldest entries (simple LRU approximation)
                    var toRemove = _cache
                        .OrderBy(kvp => kvp.Value.CreatedAt)
                        .Take(_cache.Count - _options.MaxCacheSize + 1)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in toRemove)
                    {
                        _cache.TryRemove(key, out _);
                    }
                }
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        var entry = new CacheEntry(value, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.Add(_options.CacheDuration));
        _cache[secretRef] = entry;
    }

    /// <summary>
    /// Cleans up expired cache entries.
    /// </summary>
    private void CleanupExpiredEntries(object? state)
    {
        if (_disposed)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var expiredKeys = _cache
            .Where(kvp => kvp.Value.ExpiresAt <= now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _cache.TryRemove(key, out _);
        }

        if (expiredKeys.Count > 0)
        {
            _logger.LogDebug("Cleaned up {Count} expired secret cache entries", expiredKeys.Count);
        }
    }

    /// <summary>
    /// Masks a secret reference for logging (removes sensitive parts).
    /// </summary>
    private static string MaskSecretReference(string secretRef)
    {
        var colonIndex = secretRef.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex <= 0)
        {
            return "***";
        }

        var prefix = secretRef[..colonIndex];
        return $"{prefix}:***";
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _cacheCleanupTimer?.Dispose();
        _cacheLock.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// Cache entry for secret values.
    /// </summary>
    private sealed record CacheEntry(string Value, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
}