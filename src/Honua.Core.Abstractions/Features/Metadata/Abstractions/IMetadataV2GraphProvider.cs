// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Metadata.Abstractions;

/// <summary>
/// Read access to the canonical Metadata v2 graph snapshot.
/// Consumers should call <see cref="GetCurrentAsync"/> once per request and reuse
/// the returned snapshot — it is immutable for its revision.
/// </summary>
public interface IMetadataV2GraphProvider
{
    /// <summary>
    /// Returns the current Metadata v2 graph snapshot.
    /// </summary>
    ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a specific revision of the graph if it is retained.
    /// </summary>
    ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-write access to the canonical Metadata v2 graph snapshot.
/// </summary>
public interface IMetadataV2GraphStore : IMetadataV2GraphProvider
{
    /// <summary>
    /// Persists a new graph snapshot. The returned snapshot reflects the persisted state.
    /// </summary>
    /// <param name="graph">Graph document to persist. The store allocates and returns its durable revision.</param>
    /// <param name="expectedEtag">Optional optimistic concurrency tag; pass null to force write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MetadataV2GraphSnapshot> SaveAsync(
        MetadataV2Graph graph,
        string? expectedEtag,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes an existing retained revision current without creating a new snapshot.
    /// </summary>
    /// <param name="revision">Retained durable revision to activate.</param>
    /// <param name="expectedCurrentEtag">Optional optimistic concurrency tag for the current pointer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MetadataV2GraphSnapshot> ActivateRevisionAsync(
        long revision,
        string? expectedCurrentEtag,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stages immutable Metadata v2 revisions without moving the current pointer, so a protected
/// change can be prepared and verified while canonical readers stay on the active revision.
/// A staged revision becomes visible only through <see cref="IMetadataV2GraphStore.ActivateRevisionAsync"/>.
/// </summary>
public interface IMetadataV2GraphRevisionStager
{
    /// <summary>
    /// Persists <paramref name="graph"/> as a new retained revision and leaves the current
    /// pointer untouched. The store allocates the revision above every retained snapshot.
    /// </summary>
    /// <param name="graph">Candidate graph document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The staged, not-yet-active snapshot.</returns>
    Task<MetadataV2GraphSnapshot> StageAsync(
        MetadataV2Graph graph,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a staged revision that is not current. Idempotent: an absent revision is a no-op.
    /// </summary>
    /// <param name="revision">Staged revision to discard.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when a retained snapshot was deleted.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the revision is current.</exception>
    Task<bool> DiscardStagedAsync(
        long revision,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reports an optimistic-concurrency conflict on the Metadata v2 current pointer: the caller's
/// expected ETag no longer matches the active revision because another writer advanced it.
/// Derives from <see cref="InvalidOperationException"/> so existing retry paths keep working.
/// </summary>
public sealed class MetadataV2GraphConcurrencyException : InvalidOperationException
{
    /// <summary>
    /// Creates a concurrency conflict for the supplied expected and observed ETags.
    /// </summary>
    public MetadataV2GraphConcurrencyException(string message, string? expectedEtag, string? actualEtag)
        : base(message)
    {
        ExpectedEtag = expectedEtag;
        ActualEtag = actualEtag;
    }

    /// <summary>
    /// ETag the caller expected to be current.
    /// </summary>
    public string? ExpectedEtag { get; }

    /// <summary>
    /// ETag that was actually current when the write was attempted.
    /// </summary>
    public string? ActualEtag { get; }
}

/// <summary>
/// Reports a graph commit whose durable outcome could not be determined while preserving
/// the pending snapshot identity needed by coordinated writers to reconcile or compensate it.
/// </summary>
public sealed class MetadataV2GraphCommitOutcomeUnknownException : Exception
{
    /// <summary>
    /// Creates an indeterminate graph-commit exception.
    /// </summary>
    public MetadataV2GraphCommitOutcomeUnknownException(
        MetadataV2GraphSnapshot pendingSnapshot,
        string transactionId,
        Exception innerException)
        : base("The Metadata v2 graph commit outcome could not be determined.", innerException)
    {
        PendingSnapshot = pendingSnapshot ?? throw new ArgumentNullException(nameof(pendingSnapshot));
        TransactionId = transactionId ?? throw new ArgumentNullException(nameof(transactionId));
    }

    /// <summary>
    /// Snapshot that may have become durable, including its reconciliation ETag.
    /// </summary>
    public MetadataV2GraphSnapshot PendingSnapshot { get; }

    /// <summary>
    /// PostgreSQL transaction identity for the indeterminate commit.
    /// </summary>
    public string TransactionId { get; }
}
