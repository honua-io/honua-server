// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.TileCachePackage.Domain;

namespace Honua.Core.Features.TileCachePackage.Abstractions;

/// <summary>
/// Imports an Esri tile/vector-tile cache package (<c>.tpk</c>/<c>.tpkx</c>/<c>.vtpk</c>)
/// into Honua's tile catalog and binds it to a served tileset (#1269). The service
/// reads the documented package layout via <see cref="ITileCachePackageReader"/> and
/// persists tiles through the shared <c>IOgcTileCacheSink</c>, so re-imports are
/// idempotent.
/// </summary>
public interface ITileCachePackageImportService
{
    /// <summary>
    /// Import a tile-cache package and bind it to a served tileset.
    /// </summary>
    /// <param name="request">Import request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The import result, including the served tile-cache identifier.</returns>
    Task<TileCachePackageImportResult> ImportAsync(
        TileCachePackageImportRequest request,
        CancellationToken cancellationToken = default);
}
