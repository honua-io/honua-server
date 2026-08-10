// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Captures the current server state of the features a replica upload has just been found to conflict
/// with, so the durable conflict record carries the state the operator will later be asked to keep or
/// discard. Supplied by the protocol adapter, which owns the wire shape of a feature.
/// </summary>
/// <remarks>
/// Invoked by the canonical sync service <em>after</em> conflict detection and <em>before</em> the
/// uploaded edits are applied. That ordering is the point of the seam (#2430): capturing earlier — as
/// the adapter used to, before the upload pipeline ran — meant a server edit landing between the
/// capture and detection was correctly flagged as the conflict yet missing from the snapshot, so a
/// later keep-server resolution restored a state that silently discarded it. The change log cannot be
/// used to detect that after the fact because it collapses to the latest change per feature, so the
/// snapshot has to be taken at the right moment instead.
/// </remarks>
public interface IReplicaServerStateCapturer
{
    /// <summary>
    /// Reads the current server state of each requested feature, keyed by service-local layer id and
    /// object id. Features the server has already deleted yield no entry.
    /// </summary>
    /// <param name="targets">The conflicting features to capture.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyDictionary<(int PublicLayerId, long ObjectId), string>> CaptureAsync(
        ImmutableArray<ReplicaConflictCaptureTarget> targets,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the optimistic-concurrency token of each requested feature, keyed by object id. Features
    /// the server has already deleted yield no entry.
    /// </summary>
    /// <remarks>
    /// Used under manual review to bind the uploaded edits that detection judged non-conflicting to the
    /// state detection saw: a server edit committing between the change-log read and the write would
    /// otherwise be overwritten by an edit that mode promises to withhold (#2430).
    /// </remarks>
    /// <param name="targets">The features whose tokens should be read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyDictionary<long, string>> CaptureTokensAsync(
        ImmutableArray<ReplicaConflictCaptureTarget> targets,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A feature whose server state should be captured for a durable conflict record.
/// </summary>
/// <param name="PublicLayerId">Service-local layer id.</param>
/// <param name="StorageLayerId">Storage-layer id used to read the feature.</param>
/// <param name="ObjectId">Stable object id of the conflicting feature.</param>
public readonly record struct ReplicaConflictCaptureTarget(
    int PublicLayerId,
    int StorageLayerId,
    long ObjectId);
