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
/// Imports OGC WCS / OGC API Coverages GeoTIFF/COG coverages into Honua's raster store
/// using a slice-1 migration source inventory as the deterministic selection contract.
/// </summary>
public interface IOgcCoverageImportService
{
    /// <summary>
    /// Build a deterministic migration manifest for the selected coverages and,
    /// when <see cref="OgcCoverageImportRequest.ApplyMode"/> is set, stream the
    /// GeoTIFF or Cloud Optimized GeoTIFF bytes into the configured raster store.
    /// </summary>
    /// <param name="request">Import request containing inventory and selection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OgcCoverageImportResult> ImportAsync(
        OgcCoverageImportRequest request,
        CancellationToken cancellationToken = default);
}
