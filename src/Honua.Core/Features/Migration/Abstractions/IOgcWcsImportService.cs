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
/// Imports coverages from a legacy OGC Web Coverage Service (WCS 1.x or 2.x)
/// endpoint. WCS is the older XML-over-HTTP coverage protocol that many
/// production stacks still expose alongside, or instead of, OGC API Coverages.
/// </summary>
/// <remarks>
/// Slice 3 of issue #1030. The WCS service classifies the requested output
/// format — <c>image/tiff</c> is the deterministic happy-path; any other
/// format (NetCDF, HDF, GML coverage, vendor-specific) routes the coverage
/// to manual review. The actual catalog ingestion is delegated to the
/// slice-2 <see cref="IOgcCoverageImportService"/> so there is exactly one
/// raster sink in the system.
/// </remarks>
public interface IOgcWcsImportService
{
    /// <summary>
    /// Plan or apply a WCS coverage import using the slice-1 migration inventory
    /// as the selection contract. When <see cref="OgcWcsImportRequest.ApplyMode"/>
    /// is set the underlying coverage import service streams the GeoTIFF response
    /// into the Honua raster store.
    /// </summary>
    /// <param name="request">WCS import request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OgcWcsImportResult> ImportAsync(
        OgcWcsImportRequest request,
        CancellationToken cancellationToken = default);
}
