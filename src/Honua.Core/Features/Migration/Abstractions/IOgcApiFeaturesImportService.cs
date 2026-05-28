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
/// Imports features from an OGC API Features collection into the Honua catalog via a configured sink.
/// Paginates the source items endpoint, projects features deterministically, and emits an
/// <see cref="OgcApiFeaturesImportResult"/> suitable for operator dashboards and migration evidence.
/// </summary>
public interface IOgcApiFeaturesImportService
{
    /// <summary>
    /// Imports a single OGC API Features collection.
    /// </summary>
    /// <param name="request">Import request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Import outcome including counts, warnings, and any errors.</returns>
    Task<OgcApiFeaturesImportResult> ImportCollectionAsync(
        OgcApiFeaturesImportRequest request,
        CancellationToken cancellationToken = default);
}
