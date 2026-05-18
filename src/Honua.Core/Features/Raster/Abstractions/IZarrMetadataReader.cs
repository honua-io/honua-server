// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Reads Zarr store metadata (v2 .zarray / .zattrs / .zgroup JSON) via the
/// shared <see cref="ICloudRangeReader"/> abstraction.
/// </summary>
public interface IZarrMetadataReader
{
    /// <summary>
    /// Reads and parses Zarr store metadata starting at the supplied root path.
    /// </summary>
    /// <param name="reader">Cloud range reader for the backing object store.</param>
    /// <param name="bucket">Bucket or container name.</param>
    /// <param name="rootPath">Root key/prefix containing the .zgroup or .zarray document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Parsed store metadata describing every array discovered under the root.</returns>
    Task<ZarrStoreMetadata> ReadMetadataAsync(
        ICloudRangeReader reader,
        string bucket,
        string rootPath,
        CancellationToken cancellationToken = default);
}
