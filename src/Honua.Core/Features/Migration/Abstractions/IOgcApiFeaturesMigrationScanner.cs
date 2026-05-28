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
/// Scans OGC API Features endpoints and produces deterministic migration planning artifacts.
/// </summary>
public interface IOgcApiFeaturesMigrationScanner
{
    /// <summary>
    /// Scan an OGC API Features landing page into the shared source inventory contract.
    /// </summary>
    /// <param name="request">OGC API Features scan request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Normalized source inventory artifact.</returns>
    Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
        OgcApiFeaturesScanRequest request,
        CancellationToken cancellationToken = default);
}
