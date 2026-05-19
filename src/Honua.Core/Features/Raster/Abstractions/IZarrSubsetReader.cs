// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Reads bounded chunk-aligned subsets from a Zarr store.
/// </summary>
public interface IZarrSubsetReader
{
    /// <summary>
    /// Reads a bounded subset of a single Zarr array variable.
    /// </summary>
    /// <param name="reader">Cloud range reader for the backing object store.</param>
    /// <param name="bucket">Bucket or container name.</param>
    /// <param name="rootPath">Root key/prefix containing the array metadata.</param>
    /// <param name="metadata">Previously discovered store metadata.</param>
    /// <param name="request">Subset request expressed as inclusive start / exclusive stop per dimension.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Subset payload as a contiguous little-endian buffer.</returns>
    Task<ZarrSubsetResult> ReadSubsetAsync(
        ICloudRangeReader reader,
        string bucket,
        string rootPath,
        ZarrStoreMetadata metadata,
        ZarrSubsetRequest request,
        CancellationToken cancellationToken = default);
}
