// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.Protocols.Cog;

/// <summary>Ensures all metadata reads use the object identity captured before the scan.</summary>
internal sealed class CogPinnedRangeReader(ICloudRangeReader inner, string expectedETag) : ICloudRangeReader
{
    public CloudStorageProvider Provider => inner.Provider;

    public Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length,
        CancellationToken cancellationToken = default)
        => inner.ReadRangeAsync(bucket, key, offset, length, expectedETag, cancellationToken);

    public Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, string etag,
        CancellationToken cancellationToken = default)
        => inner.ReadRangeAsync(bucket, key, offset, length, expectedETag, cancellationToken);

    public async Task<Stream> ReadRangeStreamAsync(string bucket, string key, long offset, int length,
        CancellationToken cancellationToken = default)
        => new MemoryStream(await ReadRangeAsync(bucket, key, offset, length, cancellationToken).ConfigureAwait(false), writable: false);

    public Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default)
        => inner.GetObjectSizeAsync(bucket, key, cancellationToken);

    public Task<CloudObjectMetadata> GetObjectMetadataAsync(string bucket, string key,
        CancellationToken cancellationToken = default)
        => inner.GetObjectMetadataAsync(bucket, key, cancellationToken);
}
