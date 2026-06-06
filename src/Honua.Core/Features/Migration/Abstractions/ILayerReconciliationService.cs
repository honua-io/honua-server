// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Abstractions;

/// <summary>
/// Runs per-layer reconciliation checks (count, geometry validity, content/schema, extent)
/// against published Honua catalog layers immediately after the apply step of a migration
/// run and emits a deterministic <see cref="MigrationReconciliationArtifact"/> describing
/// each layer's outcome.
/// </summary>
/// <remarks>
/// <para>
/// This service is the server-side counterpart to the SDK <c>reconcile.ts</c> probe: it
/// confirms that what landed on the Honua target lines up with the source inventory before
/// a migration run is allowed to transition to <c>Completed</c>. Hard-check failures gate
/// the run to <c>NeedsReview</c> so an operator can audit the discrepancy before claiming
/// success.
/// </para>
/// <para>
/// Source-side facts (counts, extents, field names, snapshot filter) must be captured by
/// the caller from the migration inventory or apply-time snapshot. The service does not
/// re-issue source HTTP calls — this keeps the reconciliation deterministic against the
/// apply-time snapshot and avoids drift-induced false positives.
/// </para>
/// </remarks>
public interface ILayerReconciliationService
{
    /// <summary>
    /// Run reconciliation checks for every layer in the request and aggregate the result.
    /// </summary>
    /// <param name="request">Per-run reconciliation request including the per-layer source snapshot.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Deterministic reconciliation artifact for the run.</returns>
    Task<MigrationReconciliationArtifact> ReconcileAsync(
        LayerReconciliationRequest request,
        CancellationToken cancellationToken = default);
}
