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
/// Imports OGC Web Feature Service (WFS) feature data into Honua PostGIS.
/// </summary>
public interface IOgcWfsImportService
{
    /// <summary>
    /// Import selected feature types from a WFS endpoint into Honua PostGIS, producing
    /// a deterministic manifest and an initial parity record.
    /// </summary>
    /// <param name="request">WFS import request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Per-feature-type outcomes plus the migration manifest and parity artifacts.</returns>
    Task<OgcWfsImportResult> ImportFeaturesAsync(
        OgcWfsImportRequest request,
        CancellationToken cancellationToken = default);
}
