// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain;

namespace Honua.Core.Features.Metadata.Abstractions;

/// <summary>
/// Abstraction for storing and querying GitOps watch configurations and change records.
/// </summary>
public interface IGitOpsWatchStore
{
    /// <summary>
    /// Creates or replaces the watch configuration (single-config model).
    /// </summary>
    Task<GitOpsWatchConfig> UpsertConfigAsync(GitOpsWatchConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current watch configuration, if one exists.
    /// </summary>
    Task<GitOpsWatchConfig?> GetConfigAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the current watch configuration.
    /// </summary>
    Task<bool> DeleteConfigAsync(Guid configId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the last known commit SHA and poll timestamp for a configuration.
    /// </summary>
    Task<bool> UpdatePollStateAsync(Guid configId, string commitSha, DateTimeOffset polledAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to acquire the processing lease for a specific configuration commit.
    /// </summary>
    Task<bool> TryAcquireCommitProcessingLeaseAsync(
        Guid configId,
        string commitSha,
        Guid leaseId,
        DateTimeOffset acquiredAt,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a leased commit as observed and releases the processing lease atomically.
    /// </summary>
    Task<bool> CompleteCommitProcessingAsync(
        Guid configId,
        string commitSha,
        Guid leaseId,
        DateTimeOffset polledAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the processing lease for a commit that should be retried later.
    /// </summary>
    Task<bool> ReleaseCommitProcessingLeaseAsync(
        Guid configId,
        string commitSha,
        Guid leaseId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a detected change from the watched repository.
    /// </summary>
    Task<GitOpsChangeRecord> CreateChangeRecordAsync(GitOpsChangeRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific change record by its identifier.
    /// </summary>
    Task<GitOpsChangeRecord?> GetChangeRecordAsync(Guid changeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists change records with pagination, ordered by detection time descending.
    /// </summary>
    Task<IReadOnlyList<GitOpsChangeRecord>> ListChangeRecordsAsync(int limit = 100, int offset = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the status and outcome fields of a change record identified by its pending approval reference.
    /// Used by the approval workflow to finalize GitOps change records after approve/reject decisions.
    /// </summary>
    Task<bool> UpdateChangeRecordByApprovalIdAsync(
        Guid pendingApprovalId,
        GitOpsChangeStatus newStatus,
        string? applySummary,
        string? errorMessage,
        DateTimeOffset? appliedAt,
        CancellationToken cancellationToken = default);
}
