// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>
/// Scans OGC service endpoints and produces deterministic migration planning artifacts.
/// </summary>
public interface IOgcServiceMigrationScanner
{
    /// <summary>
    /// Scan an OGC WFS, WMS, WMTS, WCS, or OGC API Coverages endpoint into the shared source inventory contract.
    /// </summary>
    /// <param name="request">OGC service scan request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Normalized source inventory artifact.</returns>
    Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
        OgcServiceScanRequest request,
        CancellationToken cancellationToken = default);
}
