// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>Canonical service association checks shared by version discovery and data adapters.</summary>
public static class VersionServiceScope
{
    /// <summary>Requires atomic service-bound maintenance; never falls back to unscoped execution.</summary>
    public static IVersionServiceMaintenanceManager RequireServiceMaintenance(this IVersionManager manager)
        => manager as IVersionServiceMaintenanceManager
            ?? throw new NotSupportedException("The provider does not support service-bound version maintenance.");

    /// <summary>Tests the persisted canonical identity; unscoped legacy versions never match implicitly.</summary>
    /// <param name="version">Persisted branch descriptor.</param>
    /// <param name="serviceId">Validated canonical service identity.</param>
    /// <returns>Whether the branch belongs to this service.</returns>
    public static bool BelongsTo(GdbVersion version, string serviceId)
        => !string.IsNullOrWhiteSpace(serviceId)
            && string.Equals(version.ServiceId, serviceId, StringComparison.Ordinal);

    /// <summary>Lists branches associated with an already authorized service; caller visibility is a separate gate.</summary>
    /// <param name="manager">Canonical provider.</param>
    /// <param name="serviceId">Validated canonical service identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Only branches whose association exactly matches.</returns>
    public static async Task<IReadOnlyList<GdbVersion>> ListForServiceAsync(
        this IVersionManager manager, string serviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        var versions = await manager.ListAsync(cancellationToken).ConfigureAwait(false);
        return versions.Where(version => BelongsTo(version, serviceId)).ToArray();
    }

    /// <summary>Resolves name or GUID through the canonical resolver and enforces the stored service association.</summary>
    /// <param name="manager">Canonical provider.</param>
    /// <param name="serviceId">Validated canonical service identity.</param>
    /// <param name="identity">Requested version name or GUID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>DEFAULT or a matching branch; otherwise null.</returns>
    public static async Task<VersionContext?> ResolveForServiceAsync(
        this IVersionManager manager, string serviceId, string? identity, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        var resolved = await manager.ResolveAsync(identity, cancellationToken).ConfigureAwait(false);
        if (resolved is null || resolved.Value.IsDefault)
        {
            return resolved;
        }
        var descriptor = await manager.GetVersionAsync(resolved.Value.VersionId!.Value, cancellationToken).ConfigureAwait(false);
        return descriptor is { } version && BelongsTo(version, serviceId) ? resolved : null;
    }
}
