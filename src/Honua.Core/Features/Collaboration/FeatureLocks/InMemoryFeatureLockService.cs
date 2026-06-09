// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;

namespace Honua.Core.Features.Collaboration.FeatureLocks;

/// <summary>
/// In-memory implementation of <see cref="IFeatureLockService"/> suitable for
/// single-node deployments and tests.
/// </summary>
public sealed class InMemoryFeatureLockService : IFeatureLockService
{
    private const string DeniedReason = "Feature lock mutation requires authorized write access.";

    private readonly ConcurrentDictionary<FeatureRef, FeatureLockLease> _leases = new();
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryFeatureLockService"/> class.
    /// </summary>
    /// <param name="timeProvider">The time provider used for lease timing, or <see langword="null"/> to use the system clock.</param>
    public InMemoryFeatureLockService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public ValueTask<FeatureLockClaimResponse> ClaimAsync(
        FeatureRef feature,
        LockHolder holder,
        TimeSpan leaseDuration,
        FeatureLockAccessContext access,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature.ServiceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(feature.FeatureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(holder.HolderId);
        ValidateLeaseDuration(leaseDuration);

        if (!HasWriteAccess(access))
        {
            return ValueTask.FromResult(new FeatureLockClaimResponse(FeatureLockClaimStatus.Denied, DenialReason: DeniedReason));
        }

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            PruneExpiredCore(now);

            if (_leases.TryGetValue(feature, out var existing))
            {
                if (IsSameHolder(existing.Holder, holder))
                {
                    var renewed = RenewLease(existing, holder, leaseDuration, now);
                    _leases[feature] = renewed;
                    return ValueTask.FromResult(new FeatureLockClaimResponse(FeatureLockClaimStatus.Renewed, Lease: renewed));
                }

                return ValueTask.FromResult(new FeatureLockClaimResponse(
                    FeatureLockClaimStatus.HeldByOther,
                    Error: FeatureLockHeldError.FromLease(existing)));
            }

            var lease = new FeatureLockLease(
                Guid.NewGuid().ToString("N"),
                feature,
                holder,
                now,
                now.Add(leaseDuration));
            _leases[feature] = lease;

            return ValueTask.FromResult(new FeatureLockClaimResponse(FeatureLockClaimStatus.Claimed, Lease: lease));
        }
    }

    /// <inheritdoc />
    public ValueTask<FeatureLockRenewResponse> RenewAsync(
        FeatureRef feature,
        LockHolder holder,
        TimeSpan leaseDuration,
        FeatureLockAccessContext access,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature.ServiceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(feature.FeatureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(holder.HolderId);
        ValidateLeaseDuration(leaseDuration);

        if (!HasWriteAccess(access))
        {
            return ValueTask.FromResult(new FeatureLockRenewResponse(FeatureLockRenewStatus.Denied, DenialReason: DeniedReason));
        }

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            PruneExpiredCore(now);

            if (!_leases.TryGetValue(feature, out var existing))
            {
                return ValueTask.FromResult(new FeatureLockRenewResponse(FeatureLockRenewStatus.NotFound));
            }

            if (!IsSameHolder(existing.Holder, holder))
            {
                return ValueTask.FromResult(new FeatureLockRenewResponse(
                    FeatureLockRenewStatus.HeldByOther,
                    Error: FeatureLockHeldError.FromLease(existing)));
            }

            var renewed = RenewLease(existing, holder, leaseDuration, now);
            _leases[feature] = renewed;
            return ValueTask.FromResult(new FeatureLockRenewResponse(FeatureLockRenewStatus.Renewed, Lease: renewed));
        }
    }

    /// <inheritdoc />
    public ValueTask<FeatureLockReleaseResponse> ReleaseAsync(
        FeatureRef feature,
        LockHolder holder,
        FeatureLockAccessContext access,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature.ServiceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(feature.FeatureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(holder.HolderId);

        if (!HasWriteAccess(access))
        {
            return ValueTask.FromResult(new FeatureLockReleaseResponse(
                FeatureLockReleaseStatus.Denied,
                feature,
                DenialReason: DeniedReason));
        }

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            PruneExpiredCore(now);

            if (!_leases.TryGetValue(feature, out var existing))
            {
                return ValueTask.FromResult(new FeatureLockReleaseResponse(FeatureLockReleaseStatus.NotFound, feature));
            }

            if (!IsSameHolder(existing.Holder, holder))
            {
                return ValueTask.FromResult(new FeatureLockReleaseResponse(
                    FeatureLockReleaseStatus.HeldByOther,
                    feature,
                    Error: FeatureLockHeldError.FromLease(existing)));
            }

            _leases.TryRemove(feature, out _);
            return ValueTask.FromResult(new FeatureLockReleaseResponse(
                FeatureLockReleaseStatus.Released,
                feature,
                ReleasedLease: existing));
        }
    }

    /// <inheritdoc />
    public ValueTask<FeatureLockLease?> GetActiveLeaseAsync(
        FeatureRef feature,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature.ServiceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(feature.FeatureId);

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            PruneExpiredCore(now);

            return ValueTask.FromResult(_leases.TryGetValue(feature, out var lease) ? lease : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> PruneExpiredAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(PruneExpiredCore(_timeProvider.GetUtcNow()));
        }
    }

    private int PruneExpiredCore(DateTimeOffset now)
    {
        var removed = 0;

        foreach (var (feature, lease) in _leases)
        {
            if (lease.ExpiresAt > now)
            {
                continue;
            }

            if (_leases.TryRemove(feature, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    private static FeatureLockLease RenewLease(
        FeatureLockLease existing,
        LockHolder holder,
        TimeSpan leaseDuration,
        DateTimeOffset now)
        => existing with
        {
            Holder = holder,
            RenewedAt = now,
            ExpiresAt = now.Add(leaseDuration)
        };

    private static bool HasWriteAccess(FeatureLockAccessContext access)
        => access.IsAuthorized && access.CanWrite;

    private static bool IsSameHolder(LockHolder existing, LockHolder candidate)
    {
        if (!string.Equals(existing.HolderId, candidate.HolderId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(existing.SessionId) || !string.IsNullOrWhiteSpace(candidate.SessionId))
        {
            return string.Equals(existing.SessionId, candidate.SessionId, StringComparison.Ordinal);
        }

        if (!string.IsNullOrWhiteSpace(existing.TenantId) || !string.IsNullOrWhiteSpace(candidate.TenantId))
        {
            return string.Equals(existing.TenantId, candidate.TenantId, StringComparison.Ordinal);
        }

        return true;
    }

    private static void ValidateLeaseDuration(TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), leaseDuration, "Lease duration must be greater than zero.");
        }
    }
}
