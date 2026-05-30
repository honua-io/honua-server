// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.FileStorage;

/// <summary>
/// In-memory implementation of upload progress store.
/// Note: This implementation does not persist across application restarts.
/// For production scenarios, consider implementing a distributed cache-backed version.
/// </summary>
internal sealed class InMemoryUploadProgressStore : IUploadProgressStore
{
    private readonly ConcurrentDictionary<string, (UploadProgress Progress, DateTimeOffset ExpiresAt)> _progressStore = new();

    /// <inheritdoc />
    public Task SetProgressAsync(string uploadId, UploadProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        ArgumentNullException.ThrowIfNull(progress);

        var expiresAt = DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromHours(1)); // Default 1 hour TTL
        _progressStore.AddOrUpdate(uploadId, (progress, expiresAt), (_, _) => (progress, expiresAt));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<UploadProgress?> GetProgressAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);

        CleanupExpiredEntries();

        if (_progressStore.TryGetValue(uploadId, out var entry))
        {
            if (entry.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return Task.FromResult<UploadProgress?>(entry.Progress);
            }

            // Entry has expired, remove it
            _progressStore.TryRemove(uploadId, out _);
        }

        return Task.FromResult<UploadProgress?>(null);
    }

    /// <inheritdoc />
    public Task DeleteProgressAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);

        _progressStore.TryRemove(uploadId, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetActiveUploadIdsAsync(CancellationToken cancellationToken = default)
    {
        CleanupExpiredEntries();

        var activeIds = _progressStore
            .Where(kvp => kvp.Value.ExpiresAt > DateTimeOffset.UtcNow)
            .Select(kvp => kvp.Key)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(activeIds);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default)
    {
        CleanupExpiredEntries();

        var activeUploads = _progressStore
            .Where(kvp => kvp.Value.ExpiresAt > DateTimeOffset.UtcNow)
            .Select(kvp => kvp.Value.Progress)
            .ToList();

        return Task.FromResult<IReadOnlyList<UploadProgress>>(activeUploads);
    }

    /// <summary>
    /// Remove expired entries from the store.
    /// </summary>
    private void CleanupExpiredEntries()
    {
        var now = DateTimeOffset.UtcNow;
        var expiredKeys = _progressStore
            .Where(kvp => kvp.Value.ExpiresAt <= now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _progressStore.TryRemove(key, out _);
        }
    }
}
