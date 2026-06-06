// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Shared registry for branch-versioned editing. Persists named branch versions per
/// service and resolves the <c>gdbVersion</c> parameter supplied by GeoServices clients
/// to the storage layer id that isolates the version's feature rows.
/// <para>
/// The implicit <c>DEFAULT</c> version (also accepted as the <c>sde.DEFAULT</c> alias,
/// a null/empty value, or the literal <c>DEFAULT</c>) always resolves to the base storage
/// layer id and is never persisted. A named branch version resolves to a distinct
/// synthetic storage layer id so that edits and reads route correctly and stay isolated
/// from DEFAULT. Because the canonical query, edit, and change-tracking pipelines are all
/// keyed on the storage layer id, branch versions are tracked and synchronised by the
/// existing incremental replication path without further branch-specific code.
/// </para>
/// </summary>
public interface IBranchVersionStore
{
    /// <summary>
    /// Determines whether a version name refers to the implicit DEFAULT version.
    /// Accepts a null/empty value, the literal <c>DEFAULT</c>, and the <c>sde.DEFAULT</c>
    /// alias, all case-insensitively.
    /// </summary>
    /// <param name="versionName">The raw <c>gdbVersion</c> value, or null.</param>
    /// <returns><see langword="true"/> when the version is DEFAULT; otherwise <see langword="false"/>.</returns>
    static bool IsDefaultVersion(string? versionName)
        => string.IsNullOrWhiteSpace(versionName)
           || versionName.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase)
           || versionName.Equals("sde.DEFAULT", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a named branch version for a service forked from the supplied base storage
    /// layer id, allocating a distinct synthetic branch storage layer id. The operation is
    /// idempotent: if the named version already exists for the service it is returned
    /// unchanged.
    /// </summary>
    /// <param name="serviceId">Feature service identifier.</param>
    /// <param name="versionName">Branch version name (must not be a DEFAULT alias).</param>
    /// <param name="baseLayerId">Base (DEFAULT) storage layer id to fork from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted branch version.</returns>
    Task<BranchVersion> CreateVersionAsync(
        string serviceId,
        string versionName,
        int baseLayerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a named branch version for a service, or <see langword="null"/> when no
    /// version with that name exists.
    /// </summary>
    /// <param name="serviceId">Feature service identifier.</param>
    /// <param name="versionName">Branch version name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<BranchVersion?> GetVersionAsync(
        string serviceId,
        string versionName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the named branch versions registered for a service.
    /// </summary>
    /// <param name="serviceId">Feature service identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<BranchVersion>> ListVersionsAsync(
        string serviceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a <c>gdbVersion</c> value to the effective storage layer id for reads and
    /// edits. DEFAULT versions resolve to <paramref name="baseLayerId"/>. A registered named
    /// version resolves to its branch storage layer id. An unknown named version returns
    /// <see langword="null"/> so the caller can reject the request.
    /// </summary>
    /// <param name="serviceId">Feature service identifier.</param>
    /// <param name="versionName">The raw <c>gdbVersion</c> value, or null for DEFAULT.</param>
    /// <param name="baseLayerId">Base (DEFAULT) storage layer id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The effective storage layer id, or <see langword="null"/> when the named version is unknown.</returns>
    Task<int?> ResolveBranchLayerIdAsync(
        string serviceId,
        string? versionName,
        int baseLayerId,
        CancellationToken cancellationToken = default);
}
