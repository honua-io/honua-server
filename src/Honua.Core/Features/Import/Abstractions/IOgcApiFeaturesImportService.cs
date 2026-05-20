// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Abstractions;

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
