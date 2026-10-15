// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Honua.Core.Features.EmbedGovernance.Abstractions;
using Honua.Core.Features.EmbedGovernance.Domain;

namespace Honua.Core.Features.EmbedGovernance;

/// <summary>
/// In-memory <see cref="IEmbedKeyStore"/>. Suitable as the default issuance
/// store; a durable provider can replace it when persistence is configured.
/// </summary>
public sealed class InMemoryEmbedKeyStore : IEmbedKeyStore
{
    private readonly ConcurrentDictionary<Guid, EmbedKeyRecord> _keys = new();
    private readonly ConcurrentDictionary<Guid, RateWindow> _windows = new();
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates the store.
    /// </summary>
    /// <param name="timeProvider">Clock abstraction; defaults to system time.</param>
    public InMemoryEmbedKeyStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<EmbedKeyRecord>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<EmbedKeyRecord> result = _keys.Values
            .OrderBy(key => key.CreatedAt)
            .ToList()
            .AsReadOnly();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<EmbedKeyCreateResult> CreateAsync(
        string name,
        EmbedKeyScope scope,
        DateTimeOffset? expiresAt,
        string? createdBy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(scope);

        var now = _timeProvider.GetUtcNow();
        var generated = EmbedKeyMaterial.Generate();
        var record = new EmbedKeyRecord
        {
            Id = Guid.NewGuid(),
            Name = name,
            KeyPrefix = EmbedKeyMaterial.DisplayPrefix(generated),
            KeyHash = EmbedKeyMaterial.Hash(generated),
            Scope = scope,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = expiresAt,
            CreatedBy = createdBy,
        };

        if (!_keys.TryAdd(record.Id, record))
        {
            throw new InvalidOperationException("Generated duplicate embed key identifier.");
        }

        return Task.FromResult(new EmbedKeyCreateResult(record, generated));
    }

    /// <inheritdoc />
    public Task<EmbedKeyRecord?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _keys.TryGetValue(id, out var record);
        return Task.FromResult(record);
    }

    /// <inheritdoc />
    public Task<EmbedKeyCreateResult?> RotateAsync(Guid id, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_keys.TryGetValue(id, out var existing) || existing.RevokedAt is not null)
            {
                return Task.FromResult<EmbedKeyCreateResult?>(null);
            }

            var now = _timeProvider.GetUtcNow();
            var generated = EmbedKeyMaterial.Generate();
            var updated = existing with
            {
                KeyPrefix = EmbedKeyMaterial.DisplayPrefix(generated),
                KeyHash = EmbedKeyMaterial.Hash(generated),
                UpdatedAt = now,
                RotatedAt = now,
                LastUsedAt = null,
            };

            // Replace only the snapshot checked above. An unconditional write would put the
            // previous secret back over a concurrent revoke or a newer rotation.
            if (_keys.TryUpdate(id, updated, existing))
            {
                _windows.TryRemove(id, out _);
                return Task.FromResult<EmbedKeyCreateResult?>(new EmbedKeyCreateResult(updated, generated));
            }
        }
    }

    /// <inheritdoc />
    public Task<EmbedKeyRecord?> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_keys.TryGetValue(id, out var existing))
            {
                return Task.FromResult<EmbedKeyRecord?>(null);
            }

            var now = _timeProvider.GetUtcNow();
            var updated = existing with
            {
                UpdatedAt = now,
                RevokedAt = existing.RevokedAt ?? now,
            };

            // A concurrent usage or rotation write replaced the snapshot: revoke the current
            // record rather than report a revocation that was never stored.
            if (_keys.TryUpdate(id, updated, existing))
            {
                return Task.FromResult<EmbedKeyRecord?>(updated);
            }
        }
    }

    /// <inheritdoc />
    public Task<EmbedKeyValidationResult?> ValidateAsync(string keyMaterial, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(keyMaterial))
        {
            return Task.FromResult<EmbedKeyValidationResult?>(null);
        }

        var providedHash = EmbedKeyMaterial.Hash(keyMaterial);
        var now = _timeProvider.GetUtcNow();

        // Retries a contended key update and returns the first winning record.
        foreach (var candidate in _keys.Values)
        {
            var record = candidate;
            while (record is not null
                && record.GetStatus(now) == EmbedKeyStatus.Active
                && CryptographicOperations.FixedTimeEquals(providedHash, record.KeyHash))
            {
                var updated = record with
                {
                    LastUsedAt = now,
                    UpdatedAt = now,
                };
                if (_keys.TryUpdate(record.Id, updated, record))
                {
                    return Task.FromResult<EmbedKeyValidationResult?>(new EmbedKeyValidationResult(updated));
                }

                // A concurrent write won the update. Never return the stale snapshot: re-check
                // the current record, so a revoke or rotate still denies while a sibling
                // request's usage write does not.
                record = _keys.TryGetValue(record.Id, out var current) ? current : null;
            }
        }

        return Task.FromResult<EmbedKeyValidationResult?>(null);
    }

    /// <inheritdoc />
    public Task<int> RecordRequestAsync(Guid id, TimeSpan window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (window <= TimeSpan.Zero)
        {
            window = TimeSpan.FromMinutes(1);
        }

        var now = _timeProvider.GetUtcNow();
        var updated = _windows.AddOrUpdate(
            id,
            _ => new RateWindow(now, 1),
            (_, current) => current.WindowStart + window <= now
                ? new RateWindow(now, 1)
                : current with { Count = current.Count + 1 });

        return Task.FromResult(updated.Count);
    }

    private sealed record RateWindow(DateTimeOffset WindowStart, int Count);
}
