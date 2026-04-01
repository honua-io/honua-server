// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Google.Cloud.Storage.V1;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.FileStorage;

/// <summary>
/// Google Cloud Storage implementation of <see cref="ICloudRangeReader"/> using native range requests.
/// </summary>
internal sealed class GcsRangeReader : ICloudRangeReader
{
    private readonly StorageClient _client;

    public GcsRangeReader(StorageClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <inheritdoc />
    public CloudStorageProvider Provider => CloudStorageProvider.GoogleCloudStorage;

    /// <inheritdoc />
    public async Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream(length);
        await _client.DownloadObjectAsync(
            bucket,
            key,
            ms,
            new DownloadObjectOptions { Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + length - 1) },
            cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }

    /// <inheritdoc />
    public async Task<Stream> ReadRangeStreamAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
    {
        var ms = new MemoryStream(length);
        await _client.DownloadObjectAsync(
            bucket,
            key,
            ms,
            new DownloadObjectOptions { Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + length - 1) },
            cancellationToken).ConfigureAwait(false);
        ms.Position = 0;
        return ms;
    }

    /// <inheritdoc />
    public async Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var obj = await _client.GetObjectAsync(bucket, key, cancellationToken: cancellationToken).ConfigureAwait(false);
        return (long)(obj.Size ?? 0UL);
    }
}
