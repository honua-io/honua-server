// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Reads COG structure from cloud storage via range requests.
/// </summary>
public interface ICogMetadataReader
{
    /// <summary>
    /// Reads and parses COG metadata (IFD chain, tile offsets, geo keys) from a cloud-hosted COG.
    /// </summary>
    /// <param name="reader">Cloud range reader for the object's storage provider</param>
    /// <param name="bucket">Bucket or container name</param>
    /// <param name="key">Object key or blob path</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Parsed COG metadata including overview levels and tile offset arrays</returns>
    Task<CogMetadata> ReadMetadataAsync(
        ICloudRangeReader reader,
        string bucket,
        string key,
        CancellationToken cancellationToken = default);
}
