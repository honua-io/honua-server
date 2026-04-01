// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Abstractions;

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
}
