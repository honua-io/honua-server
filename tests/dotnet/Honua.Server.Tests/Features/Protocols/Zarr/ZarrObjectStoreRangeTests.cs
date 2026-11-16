// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.FileStorage;
using Honua.TestKit.Attributes;
using Honua.TestKit.Formats;

namespace Honua.Server.Tests.Features.Protocols.Zarr;

[Collection("Emulators")]
public sealed class ZarrObjectStoreRangeTests
{
    private const string BucketEnv = "HONUA_TEST_S3_BUCKET";
    private const string RegionEnv = "HONUA_TEST_S3_REGION";
    private const string AccessKeyEnv = "HONUA_TEST_S3_ACCESS_KEY";
    private const string SecretKeyEnv = "HONUA_TEST_S3_SECRET_KEY";
    private const string ServiceUrlEnv = "HONUA_TEST_S3_SERVICE_URL";
    private const string ForcePathStyleEnv = "HONUA_TEST_S3_FORCE_PATH_STYLE";

    private const int Grid = 8;

    private const int Chunk = 4;

    private static float Sample(int row, int col) => (row * 10f) + col;

    [EmulatorTest(BucketEnv, RegionEnv, AccessKeyEnv, SecretKeyEnv, ServiceUrlEnv, ForcePathStyleEnv)]
    public async Task ReadSubset_FromRealObjectStore_FetchesOnlyTheIntersectingChunkAndDecodesItsCells()
    {
        var options = ReadOptions();
        var root = "zarr-range/" + Guid.NewGuid().ToString("N");
        var objects = BuildCube(root);
        await UploadAsync(options, objects);

        var recorder = new RecordingRangeReader(CreateRangeReader(options));
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(recorder, options.BucketName, root);

        metadata.Srid.Should().Be(4326);
        var array = metadata.Arrays.Should().ContainSingle().Which;
        array.Name.Should().Be("temperature");
        array.Shape.Should().Equal(Grid, Grid);
        array.Chunks.Should().Equal(Chunk, Chunk);
        array.DataType.Should().Be("<f4");

        recorder.Reset();
        var subset = await new ZarrSubsetReader().ReadSubsetAsync(
            recorder,
            options.BucketName,
            root,
            metadata,
            new ZarrSubsetRequest
            {
                Variable = "temperature",
                Start = [Chunk, Chunk],
                Stop = [Grid, Grid],
            });

        subset.Shape.Should().Equal(Chunk, Chunk);
        for (var row = 0; row < Chunk; row++)
        {
            for (var col = 0; col < Chunk; col++)
            {
                var offset = ((row * Chunk) + col) * sizeof(float);
                BitConverter.ToSingle(subset.Data, offset).Should().Be(
                    Sample(Chunk + row, Chunk + col),
                    "subset cell ({0},{1}) is cube cell (row {2}, col {3})",
                    row,
                    col,
                    Chunk + row,
                    Chunk + col);
            }
        }

        recorder.RangeKeys.Should().BeEquivalentTo([root + "/temperature/1.1"]);
    }

    [EmulatorTest(BucketEnv, RegionEnv, AccessKeyEnv, SecretKeyEnv, ServiceUrlEnv, ForcePathStyleEnv)]
    public async Task ReadRange_AgainstRealObjectStore_HonoursOffsetLengthEofAndMissingKeys()
    {
        var options = ReadOptions();
        var key = "zarr-range/" + Guid.NewGuid().ToString("N") + "/payload.bin";
        var payload = new byte[512];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 7 % 251);
        }

        await UploadAsync(options, new Dictionary<string, byte[]>(StringComparer.Ordinal) { [key] = payload });
        var reader = CreateRangeReader(options);

        (await reader.GetObjectSizeAsync(options.BucketName, key)).Should().Be(payload.Length);

        // An interior window: offset 129, length 200.
        var window = await reader.ReadRangeAsync(options.BucketName, key, 129, 200);
        window.Should().Equal(payload.AsSpan(129, 200).ToArray());

        // A request that runs past the end returns the available tail, not an error and not
        // zero padding — the case the subset reader hits on a final short chunk.
        var tail = await reader.ReadRangeAsync(options.BucketName, key, payload.Length - 10, 4096);
        tail.Should().Equal(payload.AsSpan(payload.Length - 10, 10).ToArray());

        // A missing chunk must surface as FileNotFoundException; ZarrSubsetReader catches exactly
        // that to substitute the array's fill value for an unwritten chunk. It sizes a chunk with
        // GetObjectSizeAsync BEFORE it ever issues the ranged GET, so the HEAD translation is the
        // one that path actually depends on — assert both.
        var missingHead = async () => await reader.GetObjectSizeAsync(options.BucketName, key + ".absent");
        await missingHead.Should().ThrowAsync<FileNotFoundException>();

        var missingGet = async () => await reader.ReadRangeAsync(options.BucketName, key + ".absent", 0, 16);
        await missingGet.Should().ThrowAsync<FileNotFoundException>();
    }

    [EmulatorTest(BucketEnv, RegionEnv, AccessKeyEnv, SecretKeyEnv, ServiceUrlEnv, ForcePathStyleEnv)]
    public async Task ReadSubset_WithAnUnwrittenChunk_FillsThatWindowWithTheDeclaredFillValue()
    {
        var options = ReadOptions();
        var root = "zarr-range/" + Guid.NewGuid().ToString("N");
        var objects = BuildCube(root);

        // The fixture declares fill_value NaN. Drop the north-east chunk so the store is sparse.
        const string omitted = "/temperature/0.1";
        objects.Remove(root + omitted).Should().BeTrue("the fixture must contain the chunk being omitted");
        await UploadAsync(options, objects);

        var reader = CreateRangeReader(options);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, options.BucketName, root);
        var subset = await new ZarrSubsetReader().ReadSubsetAsync(
            reader,
            options.BucketName,
            root,
            metadata,
            new ZarrSubsetRequest
            {
                Variable = "temperature",
                Start = [0, 0],
                Stop = [Grid, Grid],
            });

        for (var row = 0; row < Grid; row++)
        {
            for (var col = 0; col < Grid; col++)
            {
                var actual = BitConverter.ToSingle(subset.Data, ((row * Grid) + col) * sizeof(float));
                if (row < Chunk && col >= Chunk)
                {
                    float.IsNaN(actual).Should().BeTrue(
                        "cell ({0},{1}) is inside the omitted chunk and must read as the fill value", row, col);
                }
                else
                {
                    actual.Should().Be(Sample(row, col), "cell ({0},{1}) is stored", row, col);
                }
            }
        }
    }

    private static Dictionary<string, byte[]> BuildCube(string root)
        => ZarrFixtureBuilder.BuildGroupedZlib(
            root: root,
            rows: Grid,
            cols: Grid,
            chunkRows: Chunk,
            chunkCols: Chunk,
            sample: Sample,
            srid: 4326,
            xMin: -180,
            yMin: -90,
            xMax: 180,
            yMax: 90);

    private static AwsS3RangeReader CreateRangeReader(AwsS3Options options)
        => new(CreateClient(options));

    private static async Task UploadAsync(AwsS3Options options, Dictionary<string, byte[]> objects)
    {
        using var client = CreateClient(options);
        if (!await AmazonS3Util.DoesS3BucketExistV2Async(client, options.BucketName))
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = options.BucketName });
        }

        foreach (var (key, bytes) in objects)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = options.BucketName,
                Key = key,
                InputStream = stream,
            });
        }
    }

    private static AwsS3Options ReadOptions() => new()
    {
        BucketName = RequiredEnv(BucketEnv),
        Region = RequiredEnv(RegionEnv),
        AccessKeyId = RequiredEnv(AccessKeyEnv),
        SecretAccessKey = RequiredEnv(SecretKeyEnv),
        ServiceUrl = Environment.GetEnvironmentVariable(ServiceUrlEnv),
        ForcePathStyle = bool.TryParse(Environment.GetEnvironmentVariable(ForcePathStyleEnv), out var parsed) && parsed,
    };

    private static string RequiredEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Environment variable '{name}' is required for S3 emulator tests.");
        }

        return value;
    }

    private static AmazonS3Client CreateClient(AwsS3Options options)
    {
        var config = new AmazonS3Config
        {
            RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region),
            ForcePathStyle = options.ForcePathStyle,
        };

        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
        }

        return new AmazonS3Client(options.AccessKeyId, options.SecretAccessKey, config);
    }

    private sealed class RecordingRangeReader(ICloudRangeReader inner) : ICloudRangeReader
    {
        private readonly ConcurrentBag<string> _rangeKeys = [];

        public CloudStorageProvider Provider => inner.Provider;

        public IReadOnlyCollection<string> RangeKeys => _rangeKeys;

        public void Reset() => _rangeKeys.Clear();

        public Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
        {
            _rangeKeys.Add(key);
            return inner.ReadRangeAsync(bucket, key, offset, length, cancellationToken);
        }

        public Task<Stream> ReadRangeStreamAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
        {
            _rangeKeys.Add(key);
            return inner.ReadRangeStreamAsync(bucket, key, offset, length, cancellationToken);
        }

        public Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default)
            => inner.GetObjectSizeAsync(bucket, key, cancellationToken);
    }
}
