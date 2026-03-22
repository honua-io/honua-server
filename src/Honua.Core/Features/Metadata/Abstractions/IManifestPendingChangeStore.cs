// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain;

namespace Honua.Core.Features.Metadata.Abstractions;

/// <summary>
/// Abstraction for storing and querying manifest pending approval changes.
/// </summary>
public interface IManifestPendingChangeStore
{
    /// <summary>
    /// Creates a new pending change record.
    /// </summary>
    Task<ManifestPendingChange> CreateAsync(ManifestPendingChange pendingChange, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a pending change by its identifier.
    /// </summary>
    Task<ManifestPendingChange?> GetAsync(Guid pendingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists pending changes filtered by status with a result cap.
    /// </summary>
    Task<IReadOnlyList<ManifestPendingChange>> ListAsync(ManifestApprovalStatus? status = null, int limit = 200, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the status and decision fields of a pending change.
    /// </summary>
    Task<bool> UpdateDecisionAsync(
        Guid pendingId,
        ManifestApprovalStatus status,
        string? decisionBy,
        string? decisionReason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists expired pending changes that have not yet been decided, capped at <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<ManifestPendingChange>> ListExpiredAsync(DateTimeOffset asOf, int limit = 200, CancellationToken cancellationToken = default);
}
