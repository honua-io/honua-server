// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Infrastructure.Abstractions;

public interface ICloudRangeReader
{
    CloudStorageProvider Provider { get; }

    Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default);

    // A reader that cannot check the ETag fails closed instead of returning bytes from a changed object.
    Task<byte[]> ReadRangeAsync(
        string bucket,
        string key,
        long offset,
        int length,
        string expectedETag,
        CancellationToken cancellationToken = default)
        => Task.FromException<byte[]>(new NotSupportedException(
            "This cloud range reader does not support ETag-conditional reads."));

    Task<Stream> ReadRangeStreamAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default);

    Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default);

    async Task<CloudObjectMetadata> GetObjectMetadataAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken = default)
        => new()
        {
            SizeBytes = await GetObjectSizeAsync(bucket, key, cancellationToken).ConfigureAwait(false),
        };
}
