// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Tests.Raster.ZarrParser;

/// <summary>
/// Test double simulating an object store keyed by full path. Returns
/// <see cref="FileNotFoundException"/> for missing keys so callers can probe.
/// </summary>
internal sealed class InMemoryZarrRangeReader : ICloudRangeReader
{
    private readonly Dictionary<string, byte[]> _objects;

    public InMemoryZarrRangeReader(Dictionary<string, byte[]> objects)
    {
        _objects = objects;
    }

    public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

    public Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
    {
        if (!_objects.TryGetValue(key, out var data))
        {
            throw new FileNotFoundException(key);
        }

        var startOffset = (int)offset;
        var available = data.Length - startOffset;
        if (available <= 0)
        {
            return Task.FromResult(System.Array.Empty<byte>());
        }
        var bytesToRead = System.Math.Min(length, available);
        var slice = new byte[bytesToRead];
        Buffer.BlockCopy(data, startOffset, slice, 0, bytesToRead);
        return Task.FromResult(slice);
    }

    public Task<Stream> ReadRangeStreamAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
    {
        if (!_objects.TryGetValue(key, out var data))
        {
            throw new FileNotFoundException(key);
        }
        return Task.FromResult<Stream>(new MemoryStream(data, (int)offset, System.Math.Min(length, data.Length - (int)offset)));
    }

    public Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        if (!_objects.TryGetValue(key, out var data))
        {
            throw new FileNotFoundException(key);
        }
        return Task.FromResult((long)data.Length);
    }
}
