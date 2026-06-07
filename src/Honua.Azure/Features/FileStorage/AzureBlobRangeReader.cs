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
    public async Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

        var blobClient = _serviceClient.GetBlobContainerClient(bucket).GetBlobClient(key);
        var range = new HttpRange(offset, length);

        var response = await blobClient.DownloadStreamingAsync(
            new BlobDownloadOptions { Range = range },
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

        // Buffer the range into memory and dispose the BlobDownloadStreamingResult (which owns the
        // underlying HTTP response/network resources) before returning. This makes the returned
        // stream leak-proof regardless of caller disposal behavior, matching the buffering semantics
        // of the AWS sibling AwsS3RangeReader.ReadRangeStreamAsync.
        using var result = response.Value;
        var buffer = new MemoryStream(length);
        await result.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        return buffer;
    }

    /// <inheritdoc />
    public async Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var blobClient = _serviceClient.GetBlobContainerClient(bucket).GetBlobClient(key);
        var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return properties.Value.ContentLength;
    }
}
