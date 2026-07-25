// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>Deterministic Zarr objects used by ImageServer multidimensional read tests.</summary>
internal static class ImageServerZarrTestFixture
{
    /// <summary>Builds a 3D (elevation, y, x) Zarr v2 store with a vertical axis manifest.</summary>
    public static Dictionary<string, byte[]> BuildVerticalStore(string root, int levels, int rows, int cols)
    {
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [root + "/.zgroup"] = Encoding.UTF8.GetBytes("{\"zarr_format\":2}"),
            [root + "/.zattrs"] = Encoding.UTF8.GetBytes(
                "{\"variables\":[\"temperature\"],\"primary_variable\":\"temperature\","
                + "\"crs_wkid\":4326,\"extent\":[-180,-90,180,90],"
                + "\"x_dimension\":\"x\",\"y_dimension\":\"y\","
                + "\"axes\":[{\"name\":\"elevation\",\"unit\":\"m\",\"start\":0,\"end\":1000}]}"),
        };

        var arrayRoot = root + "/temperature";
        objects[arrayRoot + "/.zarray"] = Encoding.UTF8.GetBytes(
            "{\"chunks\":[" + levels + "," + rows + "," + cols + "],\"compressor\":null,\"dtype\":\"<f4\","
            + "\"fill_value\":0,\"filters\":null,\"order\":\"C\",\"shape\":[" + levels + "," + rows + "," + cols + "],\"zarr_format\":2}");
        objects[arrayRoot + "/.zattrs"] = Encoding.UTF8.GetBytes("{\"_ARRAY_DIMENSIONS\":[\"elevation\",\"y\",\"x\"]}");

        var raw = new byte[levels * rows * cols * sizeof(float)];
        for (var level = 0; level < levels; level++)
        {
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < cols; column++)
                {
                    var offset = ((level * rows + row) * cols + column) * sizeof(float);
                    var value = (level * 1000f) + (row * 10f) + column;
                    Buffer.BlockCopy(BitConverter.GetBytes(value), 0, raw, offset, sizeof(float));
                }
            }
        }

        objects[arrayRoot + "/0.0.0"] = raw;
        return objects;
    }
}

/// <summary>Object-store double serving a fixture path-to-bytes map.</summary>
internal sealed class ImageServerFixtureRangeReader(Dictionary<string, byte[]> objects) : ICloudRangeReader
{
    public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

    public Task<byte[]> ReadRangeAsync(
        string bucket,
        string key,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        var data = Get(key);
        var start = (int)offset;
        var available = data.Length - start;
        if (available <= 0)
        {
            return Task.FromResult(Array.Empty<byte>());
        }

        var count = Math.Min(length, available);
        var slice = new byte[count];
        Buffer.BlockCopy(data, start, slice, 0, count);
        return Task.FromResult(slice);
    }

    public Task<Stream> ReadRangeStreamAsync(
        string bucket,
        string key,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        var data = Get(key);

        // Ownership transfers to the caller via the returned Stream; the caller disposes it.
        return Task.FromResult<Stream>(
            new Honua.TestKit.CallerOwnedMemoryStream(data, (int)offset, Math.Min(length, data.Length - (int)offset)));
    }
    public Task<long> GetObjectSizeAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken = default)
        => Task.FromResult((long)Get(key).Length);

    private byte[] Get(string key)
        => objects.TryGetValue(key, out var data) ? data : throw new FileNotFoundException(key);
}
