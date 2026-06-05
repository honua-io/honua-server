// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Canonical replica-upload synchronization pipeline (#1272). Resolves uploaded edits against the
/// replica's base generation cursor, detects server-side conflicts via the change log, applies
/// non-conflicting edits through the shared edit pipeline (via <see cref="IReplicaEditApplier"/>),
/// and writes durable conflict records when conflict review is supported. Protocol adapters
/// (GeoServices <c>synchronizeReplica</c> and any future replica-capable protocol) map their wire
/// parameters onto <see cref="ReplicaSyncRequest"/> and adapt the returned report; they do not
/// reimplement conflict detection or the upload cursor.
/// </summary>
public interface IReplicaSyncService
{
    /// <summary>
    /// Applies an uploaded replica synchronization: detects conflicts against the base generation,
    /// applies non-conflicting (and, under last-write-wins, conflicting) edits through the supplied
    /// applier, records durable conflicts when supported, and returns an upload report whose
    /// <see cref="ReplicaSyncUploadReport.ServerGeneration"/> is the cursor a subsequent download
    /// delta should use as its lower bound.
    /// </summary>
    /// <param name="request">Canonical sync request.</param>
    /// <param name="editApplier">
    /// Protocol-supplied applier that writes edits through the shared edit pipeline.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The upload report.</returns>
    Task<ReplicaSyncUploadReport> ApplyUploadAsync(
        ReplicaSyncRequest request,
        IReplicaEditApplier editApplier,
        CancellationToken cancellationToken = default);
}
