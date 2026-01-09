// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>
/// Service for discovering and importing data from ArcGIS Server services.
/// </summary>
public interface IEsriImportService
{
    /// <summary>
    /// Discover available layers from an ArcGIS Server service URL.
    /// </summary>
    /// <param name="request">Discovery request with service URL</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Service information including available layers</returns>
    Task<EsriServiceInfo> DiscoverServiceAsync(
        EsriDiscoveryRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Import a layer from an ArcGIS Server service into PostGIS.
    /// </summary>
    /// <param name="request">Import request with layer details</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Import result with success/failure details</returns>
    Task<EsriImportResult> ImportLayerAsync(
        EsriImportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Import a layer with progress reporting.
    /// </summary>
    /// <param name="request">Import request with layer details</param>
    /// <param name="progress">Progress reporter for tracking import status</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Import result with success/failure details</returns>
    Task<EsriImportResult> ImportLayerAsync(
        EsriImportRequest request,
        IProgress<EsriImportProgress>? progress,
        CancellationToken cancellationToken = default);
}

