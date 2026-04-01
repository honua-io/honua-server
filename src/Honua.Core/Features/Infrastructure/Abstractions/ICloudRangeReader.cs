// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Reads byte ranges from cloud-hosted objects (S3, Azure Blob, GCS).
/// Separate from <see cref="ICloudFileStorage"/> because the use case is fundamentally
/// different: low-latency partial reads vs. full file upload/download.
/// </summary>
public interface ICloudRangeReader
{
    /// <summary>
    /// Gets the cloud storage provider this reader supports.
    /// </summary>
    CloudStorageProvider Provider { get; }

    /// <summary>
    /// Reads a byte range from a cloud-hosted object.
    /// </summary>
    /// <param name="bucket">Bucket or container name</param>
    /// <param name="key">Object key or blob path</param>
    /// <param name="offset">Byte offset to start reading from</param>
    /// <param name="length">Number of bytes to read</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The requested byte range</returns>
    Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a byte range from a cloud-hosted object as a stream.
    /// </summary>
    /// <param name="bucket">Bucket or container name</param>
    /// <param name="key">Object key or blob path</param>
    /// <param name="offset">Byte offset to start reading from</param>
    /// <param name="length">Number of bytes to read</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A stream containing the requested byte range</returns>
    Task<Stream> ReadRangeStreamAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the size of a cloud-hosted object in bytes.
    /// </summary>
    /// <param name="bucket">Bucket or container name</param>
    /// <param name="key">Object key or blob path</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Object size in bytes</returns>
    Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default);
}
