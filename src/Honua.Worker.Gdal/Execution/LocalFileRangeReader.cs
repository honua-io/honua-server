// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Bounded range reader over a single local scratch file, letting the worker run the
/// shared <see cref="Honua.Core.Features.Raster.CogParser.CogRasterHeaderProbe"/>
/// against the output it just produced (grid summary for published descriptors,
/// #3089). The bucket/key/ETag identity parameters are ignored: the file is a
/// worker-private scratch artifact that cannot change identity under the probe.
/// </summary>
internal sealed class LocalFileRangeReader : ICloudRangeReader
{
    /// <summary>Placeholder identity accepted for every probe argument.</summary>
    public const string LocalIdentity = "local";

    private readonly string _path;

    public LocalFileRangeReader(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public CloudStorageProvider Provider => CloudStorageProvider.Local;

    public async Task<byte[]> ReadRangeAsync(
        string bucket,
        string key,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            _path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        if (offset >= stream.Length)
        {
            return Array.Empty<byte>();
        }

        stream.Seek(offset, SeekOrigin.Begin);
        var available = (int)Math.Min(length, stream.Length - offset);
        var buffer = new byte[available];
        await stream.ReadExactlyAsync(buffer.AsMemory(0, available), cancellationToken).ConfigureAwait(false);
        return buffer;
    }

    public Task<byte[]> ReadRangeAsync(
        string bucket,
        string key,
        long offset,
        int length,
        string expectedETag,
        CancellationToken cancellationToken = default)
        => ReadRangeAsync(bucket, key, offset, length, cancellationToken);

    public async Task<Stream> ReadRangeStreamAsync(
        string bucket,
        string key,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
        => new MemoryStream(
            await ReadRangeAsync(bucket, key, offset, length, cancellationToken).ConfigureAwait(false),
            writable: false);

    public Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default)
        => Task.FromResult(new FileInfo(_path).Length);
}
