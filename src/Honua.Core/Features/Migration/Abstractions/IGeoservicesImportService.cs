// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Abstractions;

/// <summary>
/// Service for discovering and importing data from ArcGIS Server services.
/// </summary>
public interface IGeoservicesImportService
{
    /// <summary>
    /// Discover available layers from an ArcGIS Server service URL.
    /// </summary>
    /// <param name="request">Discovery request with service URL</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Service information including available layers</returns>
    Task<GeoservicesServiceInfo> DiscoverServiceAsync(
        GeoservicesDiscoveryRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Scan an ArcGIS GeoServices source and produce a deterministic migration inventory artifact.
    /// </summary>
    /// <param name="request">Discovery request with service URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Normalized inventory artifact for planning and review.</returns>
    Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
        GeoservicesDiscoveryRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Import a layer from an ArcGIS Server service into PostGIS.
    /// </summary>
    /// <param name="request">Import request with layer details</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Import result with success/failure details</returns>
    Task<GeoservicesImportResult> ImportLayerAsync(
        GeoservicesImportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Import a layer with progress reporting.
    /// </summary>
    /// <param name="request">Import request with layer details</param>
    /// <param name="progress">Progress reporter for tracking import status</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Import result with success/failure details</returns>
    Task<GeoservicesImportResult> ImportLayerAsync(
        GeoservicesImportRequest request,
        IProgress<GeoservicesImportProgress>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Apply the simple relationship classes captured in a migration manifest
    /// to the Honua catalog so the GeoServices <c>queryRelatedRecords</c> path
    /// can resolve related features for imported ArcGIS layers (issue #1256).
    /// </summary>
    /// <remarks>
    /// The caller provides a <paramref name="publishedLayerMap"/> mapping each
    /// source resource id from the manifest (e.g.
    /// <c>resource:Inspections:layer:0</c>) to the Honua layer id assigned at
    /// publish time. Relationships whose origin or related resource is not in
    /// the map are recorded as <see cref="MigrationCatalogWriteOutcome.AlreadyExists"/>
    /// with a manual-review-style message so re-running after the missing layer
    /// is imported converges. Composite, attributed, many-to-many, and
    /// manual-review relationships are skipped here per the slice contract.
    /// </remarks>
    /// <param name="manifest">Manifest emitted by the manifest translator. Must originate from an ArcGIS source.</param>
    /// <param name="publishedLayerMap">Resolved Honua layer ids per manifest source resource id. Keys are the manifest's <c>SourceResourceId</c> values.</param>
    /// <param name="graphStore">Optional read-write Metadata v2 graph store. When <c>null</c>, only the v1 <c>honua.relationships</c> rows are written.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Per-relationship outcomes ordered by origin layer id then source relationship id.</returns>
    Task<MigrationRelationshipApplyOutcome[]> ApplyRelationshipsAsync(
        MigrationManifestArtifact manifest,
        IReadOnlyDictionary<string, int> publishedLayerMap,
        IMetadataV2GraphStore? graphStore,
        CancellationToken cancellationToken = default);
}
