// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>Atomic one-time association of an explicitly authorized legacy branch with a canonical service.</summary>
public interface IVersionServiceAssociationManager
{
    /// <summary>Associates an unscoped version without changing its identity, owner, data, or access level.</summary>
    /// <remarks>The caller must first authorize the owner/admin and the target service/resource writes.
    /// The expected owner is a concurrency guard, not a client-provided authorization grant.</remarks>
    /// <param name="versionId">Exact existing branch GUID.</param>
    /// <param name="serviceId">Validated canonical target service ID.</param>
    /// <param name="expectedOwner">Owner observed by the authorized caller.</param>
    /// <param name="permittedStorageLayers">Managed storage layers authorized for adoption in the target service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The association result; no reassignment is performed.</returns>
    Task<VersionServiceAssociationResult> AssociateLegacyVersionAsync(Guid versionId, string serviceId,
        string expectedOwner, IReadOnlyList<int> permittedStorageLayers, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of an atomic legacy branch association attempt.</summary>
public enum VersionServiceAssociationResult
{
    /// <summary>The association was persisted or already matched exactly.</summary>
    Associated,
    /// <summary>No branch exists with the requested GUID.</summary>
    Missing,
    /// <summary>Owner, state, prior association, or affected layers prevent adoption.</summary>
    Conflict
}
