// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Abstractions;

/// <summary>
/// Persistence seam for ArcGIS migration evidence (slices 1-5) so admin UIs and SDKs can
/// retrieve the manifest and post-apply parity artifact for a previously executed migration
/// run without re-scanning the source.
/// </summary>
/// <remarks>
/// The store is intentionally read-and-write capable: write paths are exercised by the slice
/// 1-5 apply/parity pipelines when persisting evidence; read paths are consumed by the slice 6
/// admin endpoints. The schema is JSONB to avoid coupling the store to manifest/parity
/// schema versioning - artifact kind/version live inside the persisted payload.
/// </remarks>
public interface IArcGisMigrationEvidenceStore
{
    /// <summary>
    /// Persist a manifest snapshot for an ArcGIS migration run. Idempotent on
    /// <paramref name="record"/>'s <c>RunId</c>.
    /// </summary>
    /// <param name="record">Run summary plus manifest payload.</param>
    /// <param name="manifest">Manifest artifact emitted by slices 2-4.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveManifestAsync(
        ArcGisMigrationRunRecord record,
        MigrationManifestArtifact manifest,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist a parity snapshot for an ArcGIS migration run. The matching manifest record
    /// must already exist; otherwise the call throws.
    /// </summary>
    /// <param name="runId">Stable run identifier.</param>
    /// <param name="parity">Parity artifact emitted by slice 5.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveParityAsync(
        string runId,
        ArcGisMigrationParityArtifact parity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// List recorded ArcGIS migration runs, newest first, optionally filtered by source URL or
    /// parity classification status.
    /// </summary>
    /// <param name="filter">Filter parameters (page, page size, source url prefix, status).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ArcGisMigrationRunListResult> ListAsync(
        ArcGisMigrationRunFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Load the manifest artifact for a run. Returns <c>null</c> when the run is unknown.
    /// </summary>
    /// <param name="runId">Stable run identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MigrationManifestArtifact?> GetManifestAsync(
        string runId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Load the parity artifact for a run. Returns <c>null</c> when the run is unknown or has
    /// no parity artifact persisted yet.
    /// </summary>
    /// <param name="runId">Stable run identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ArcGisMigrationParityArtifact?> GetParityAsync(
        string runId,
        CancellationToken cancellationToken = default);
}
