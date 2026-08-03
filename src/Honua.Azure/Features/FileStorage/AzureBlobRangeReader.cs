// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.FileStorage;

/// <summary>
/// Azure Blob Storage implementation of <see cref="ICloudRangeReader"/> using native range requests.
/// </summary>
internal sealed class AzureBlobRangeReader : ICloudRangeReader
{
    private readonly BlobServiceClient _serviceClient;

    public AzureBlobRangeReader(BlobServiceClient serviceClient)
    {
        _serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
    }

    /// <inheritdoc />
    public CloudStorageProvider Provider => CloudStorageProvider.AzureBlob;

    /// <inheritdoc />
    public Task<byte[]> ReadRangeAsync(
        string bucket,
        string key,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
        => ReadRangeCoreAsync(bucket, key, offset, length, expectedETag: null, cancellationToken);

    /// <inheritdoc />
    public Task<byte[]> ReadRangeAsync(
        string bucket,
        string key,
        long offset,
        int length,
        string expectedETag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedETag);
        return ReadRangeCoreAsync(bucket, key, offset, length, expectedETag, cancellationToken);
    }

    private async Task<byte[]> ReadRangeCoreAsync(
        string bucket,
        string key,
        long offset,
        int length,
        string? expectedETag,
        CancellationToken cancellationToken)
    {
        var blobClient = _serviceClient.GetBlobContainerClient(bucket).GetBlobClient(key);
        var range = new HttpRange(offset, length);

        var response = await blobClient.DownloadStreamingAsync(
            new BlobDownloadOptions
            {
                Range = range,
                Conditions = string.IsNullOrWhiteSpace(expectedETag)
                    ? null
                    : new BlobRequestConditions { IfMatch = new ETag(expectedETag) },
            },
            cancellationToken).ConfigureAwait(false);

        using var ms = new MemoryStream(length);
        await response.Value.Content.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }

    /// <inheritdoc />
    public async Task<Stream> ReadRangeStreamAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
    {
        var blobClient = _serviceClient.GetBlobContainerClient(bucket).GetBlobClient(key);
        var range = new HttpRange(offset, length);

        var response = await blobClient.DownloadStreamingAsync(
            new BlobDownloadOptions { Range = range },
            cancellationToken).ConfigureAwait(false);

        // Wrap the streaming result so disposing the returned stream also disposes the
        // owning BlobDownloadStreamingResult. Disposing the content stream alone does NOT
        // release the underlying HTTP response/network resources held by the result.
        return new BlobDownloadStream(response.Value);
    }

    /// <inheritdoc />
    public async Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var blobClient = _serviceClient.GetBlobContainerClient(bucket).GetBlobClient(key);
        var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return properties.Value.ContentLength;
    }

    /// <inheritdoc />
    public async Task<CloudObjectMetadata> GetObjectMetadataAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken = default)
    {
        var blobClient = _serviceClient.GetBlobContainerClient(bucket).GetBlobClient(key);
        var response = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var properties = response.Value;
        return new CloudObjectMetadata
        {
            SizeBytes = properties.ContentLength,
            Version = properties.VersionId,
            ETag = properties.ETag.ToString(),
            MediaType = properties.ContentType,
        };
    }
}
