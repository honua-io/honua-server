// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Abstractions;

/// <summary>
/// Issue #1253. Storage abstraction for footprint-driven batch migration runs
/// (<see cref="MigrationBatchRunRecord"/>) and their ordered child imports
/// (<see cref="MigrationBatchChildRecord"/>). The batch orchestrator and the
/// admin endpoints adapt over this catalog so the storage backend (Postgres
/// today) stays behind a single seam.
/// </summary>
/// <remarks>
/// Implementations must be idempotent for repeat <see cref="CreateAsync"/> calls
/// with the same batch id (re-recording an existing batch returns it untouched)
/// so the orchestrator can retry safely. Child rows are immutable except for
/// their status fields, which advance monotonically toward terminal states.
/// </remarks>
public interface IMigrationBatchRunCatalog
{
    /// <summary>
    /// Persist a new batch run plus its ordered child rows. Idempotent: if the
    /// batch id already exists, the existing batch and children are returned and
    /// the supplied children are ignored.
    /// </summary>
    /// <param name="record">Initial batch record. Status should be
    /// <see cref="MigrationBatchRunStatus.Running"/>.</param>
    /// <param name="manifestBody">Optional manifest JSON body persisted alongside
    /// the batch so relationship-apply can run after all children publish. Null
    /// when relationship-apply is not requested.</param>
    /// <param name="children">Ordered child rows (by ordinal).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MigrationBatchRunRecord> CreateAsync(
        MigrationBatchRunRecord record,
        string? manifestBody,
        IReadOnlyList<MigrationBatchChildRecord> children,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetch a single batch run by id, or null if unknown.
    /// </summary>
    Task<MigrationBatchRunRecord?> GetAsync(
        string batchId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Return the ordered child rows for a batch (by ordinal ascending). Empty
    /// when the batch is unknown.
    /// </summary>
    Task<IReadOnlyList<MigrationBatchChildRecord>> GetChildrenAsync(
        string batchId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Return the manifest JSON body persisted for a batch, or null when the
    /// batch is unknown or relationship-apply was not requested.
    /// </summary>
    Task<string?> GetManifestBodyAsync(
        string batchId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update a single child's status (and optional job id / published layer id /
    /// note). Returns the updated child, or null if the batch/ordinal is unknown.
    /// Terminal child states (succeeded, failed, cancelled) are sticky.
    /// </summary>
    Task<MigrationBatchChildRecord?> UpdateChildAsync(
        string batchId,
        int ordinal,
        MigrationBatchChildStatus status,
        string? jobId,
        int? publishedLayerId,
        string? statusNote,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update the rolled-up batch status and child counts. Returns the updated
    /// record, or null if the batch is unknown. Terminal batch states are sticky.
    /// </summary>
    Task<MigrationBatchRunRecord?> UpdateBatchAsync(
        string batchId,
        MigrationBatchRunStatus status,
        int succeededChildren,
        int failedChildren,
        int cancelledChildren,
        DateTimeOffset? completedAt,
        bool? relationshipsApplied,
        string? statusNote,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Return the ids of batches that are still running so a recovering leader can
    /// resume advancing them.
    /// </summary>
    Task<IReadOnlyList<string>> GetActiveBatchIdsAsync(
        CancellationToken cancellationToken = default);
}
