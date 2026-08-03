// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Reads byte ranges from cloud-hosted objects (S3 and Azure Blob).
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
    /// Reads a byte range only when the object still has the expected ETag.
    /// Implementations that cannot enforce the condition fail closed rather than silently
    /// reading mutable content under an identity established by an earlier metadata request.
    /// </summary>
    /// <param name="bucket">Bucket or container name</param>
    /// <param name="key">Object key or blob path</param>
    /// <param name="offset">Byte offset to start reading from</param>
    /// <param name="length">Number of bytes to read</param>
    /// <param name="expectedETag">ETag returned by the identity metadata request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The requested byte range from the matching object identity</returns>
    Task<byte[]> ReadRangeAsync(
        string bucket,
        string key,
        long offset,
        int length,
        string expectedETag,
        CancellationToken cancellationToken = default)
        => Task.FromException<byte[]>(new NotSupportedException(
            "This cloud range reader does not support ETag-conditional reads."));

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

    /// <summary>
    /// Gets bounded object identity metadata without reading the object payload.
    /// Implementations should use a provider HEAD/properties request. The default keeps
    /// existing range-reader implementations source-compatible while returning size only.
    /// </summary>
    async Task<CloudObjectMetadata> GetObjectMetadataAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken = default)
        => new()
        {
            SizeBytes = await GetObjectSizeAsync(bucket, key, cancellationToken).ConfigureAwait(false),
        };
}
