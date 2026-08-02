// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Amazon.S3;
using Amazon.S3.Model;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.FileStorage;

/// <summary>
/// AWS S3 implementation of <see cref="ICloudRangeReader"/> using native byte-range requests.
/// </summary>
internal sealed class AwsS3RangeReader : ICloudRangeReader
{
    private readonly IAmazonS3 _client;

    public AwsS3RangeReader(IAmazonS3 client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <inheritdoc />
    public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

    /// <inheritdoc />
    public async Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
    {
        var request = new GetObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ByteRange = new ByteRange(offset, offset + length - 1)
        };

        using var response = await _client.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
        using var ms = new MemoryStream(length);
        await response.ResponseStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }

    /// <inheritdoc />
    public async Task<Stream> ReadRangeStreamAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
    {
        var request = new GetObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ByteRange = new ByteRange(offset, offset + length - 1)
        };

        using var response = await _client.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
        var ms = new MemoryStream(length);
        await response.ResponseStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        ms.Position = 0;
        return ms;
    }

    /// <inheritdoc />
    public async Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var metadata = await _client.GetObjectMetadataAsync(bucket, key, cancellationToken).ConfigureAwait(false);
        return metadata.ContentLength;
    }

    /// <inheritdoc />
    public async Task<CloudObjectMetadata> GetObjectMetadataAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken = default)
    {
        var metadata = await _client.GetObjectMetadataAsync(bucket, key, cancellationToken).ConfigureAwait(false);
        var checksum = NormalizeSha256(metadata.ChecksumSHA256);
        return new CloudObjectMetadata
        {
            SizeBytes = metadata.ContentLength,
            Version = metadata.VersionId,
            ETag = metadata.ETag,
            MediaType = metadata.Headers.ContentType,
            ChecksumAlgorithm = checksum is null ? null : "sha256",
            ChecksumValue = checksum,
        };
    }

    private static string? NormalizeSha256(string? sha256)
        => string.IsNullOrWhiteSpace(sha256)
            ? null
            : Convert.ToHexString(Convert.FromBase64String(sha256));
}
