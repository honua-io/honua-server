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
using Honua.Core.Features.FileImport.Services.FileGdb;

namespace Honua.Core.Features.Migration.Abstractions;

/// <summary>
/// Exports XYZ/TMS tile caches from an OGC WMTS source for plan entries
/// classified as <c>automated</c> by the slice-3 WMTS migration planner.
/// </summary>
public interface IOgcTileCacheExportService
{
    /// <summary>
    /// Resolve the migration manifest for the requested source, then walk the
    /// classified WMTS tile-set plan entries and fetch the configured
    /// zoom-level window into Honua's tile catalog through the registered
    /// <see cref="IOgcTileCacheSink"/>. The operation is idempotent: tiles
    /// already present in the sink are left untouched.
    /// </summary>
    /// <param name="request">Tile cache export request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Per-tile-set outcomes plus the migration manifest the export was derived from.</returns>
    Task<OgcTileCacheExportResult> ExportAsync(
        OgcTileCacheExportRequest request,
        CancellationToken cancellationToken = default);
}
