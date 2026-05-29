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
/// Service for discovering and importing data from GeoServer instances.
/// </summary>
public interface IGeoServerImportService
{
    /// <summary>
    /// Discover available workspaces, datastores, layers and styles from a GeoServer instance.
    /// </summary>
    /// <param name="request">Discovery request with GeoServer REST API URL</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Service information including available resources for migration</returns>
    Task<GeoServerServiceInfo> DiscoverServiceAsync(
        GeoServerDiscoveryRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Scan a GeoServer source and produce a deterministic migration inventory artifact.
    /// </summary>
    /// <param name="request">Discovery request with GeoServer REST API URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Normalized inventory artifact for planning and review.</returns>
    Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
        GeoServerDiscoveryRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Import workspaces, datastores, layers and styles from a GeoServer instance into Honua.
    /// </summary>
    /// <param name="request">Import request with migration details</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Import result with success/failure details</returns>
    Task<GeoServerImportResult> ImportConfigurationAsync(
        GeoServerImportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Import GeoServer configuration with progress reporting.
    /// </summary>
    /// <param name="request">Import request with migration details</param>
    /// <param name="progress">Progress reporter for tracking import status</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Import result with success/failure details</returns>
    Task<GeoServerImportResult> ImportConfigurationAsync(
        GeoServerImportRequest request,
        IProgress<GeoServerImportProgress>? progress,
        CancellationToken cancellationToken = default);
}
