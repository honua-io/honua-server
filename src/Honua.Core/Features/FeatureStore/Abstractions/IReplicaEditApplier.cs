// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Applies a resolved set of uploaded replica edits for a single layer through the shared edit
/// pipeline. The concrete implementation is supplied by the protocol adapter so that uploaded edits
/// reuse the same validation, authorization, geometry conversion, telemetry, and transactional
/// outbox behavior as ordinary edits, rather than the replica path issuing its own data access.
/// </summary>
/// <remarks>
/// The canonical <see cref="IReplicaSyncService"/> owns conflict detection and the upload cursor; it
/// only decides <em>which</em> edits to apply and delegates the <em>how</em> to this applier so the
/// shared edit pipeline remains the single write path (#1272).
/// </remarks>
public interface IReplicaEditApplier
{
    /// <summary>
    /// Applies the supplied edits for a single layer through the shared edit pipeline.
    /// </summary>
    /// <param name="serviceId">Service the layer belongs to.</param>
    /// <param name="publicLayerId">Service-local layer id the edits target.</param>
    /// <param name="edits">Resolved edits to apply (conflicting edits already filtered when skipped).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The per-layer apply result.</returns>
    Task<ReplicaLayerApplyResult> ApplyAsync(
        string serviceId,
        int publicLayerId,
        ImmutableArray<ReplicaUploadEdit> edits,
        CancellationToken cancellationToken = default);
}
