// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.TileCachePackage.Domain;

namespace Honua.Core.Features.TileCachePackage.Abstractions;

/// <summary>
/// Read-only reader for Esri tile/vector-tile cache packages (<c>.tpk</c>,
/// <c>.tpkx</c>, <c>.vtpk</c>) (#1269). Implementations parse the documented
/// published cache layout (<c>root.json</c>/<c>conf.xml</c> + Compact Cache V2
/// bundles or exploded tile files) and stream the contained tiles. They do not
/// reverse-engineer proprietary internals beyond the documented cache structure
/// and never depend on licensed Esri software.
/// </summary>
public interface ITileCachePackageReader
{
    /// <summary>
    /// Determines whether the supplied file name names a tile-cache package this
    /// reader can import.
    /// </summary>
    /// <param name="fileName">Uploaded file name (with extension).</param>
    /// <returns><see langword="true"/> for <c>.tpk</c>, <c>.tpkx</c>, or <c>.vtpk</c>.</returns>
    bool CanRead(string fileName);

    /// <summary>
    /// Parse the package's tiling-scheme descriptor without reading tile bytes.
    /// </summary>
    /// <param name="package">Seekable stream over the package archive.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed descriptor.</returns>
    Task<TileCachePackageDescriptor> ReadDescriptorAsync(
        Stream package,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stream every tile contained in the package, restricted to the supplied
    /// inclusive zoom range. Coordinates are emitted in standard
    /// <c>{z}/{x}=column/{y}=row</c> form (top-left tile origin).
    /// </summary>
    /// <param name="package">Seekable stream over the package archive.</param>
    /// <param name="descriptor">Descriptor previously parsed from the package.</param>
    /// <param name="minZoom">Inclusive minimum zoom to emit.</param>
    /// <param name="maxZoom">Inclusive maximum zoom to emit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of tiles.</returns>
    IAsyncEnumerable<TileCachePackageTile> ReadTilesAsync(
        Stream package,
        TileCachePackageDescriptor descriptor,
        int minZoom,
        int maxZoom,
        CancellationToken cancellationToken = default);
}
