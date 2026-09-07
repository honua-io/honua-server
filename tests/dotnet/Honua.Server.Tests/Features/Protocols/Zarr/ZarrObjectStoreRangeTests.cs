// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// Reads a Zarr store out of a real object store (LocalStack S3) through the production
/// <see cref="AwsS3RangeReader"/>, including byte-range semantics (honua-server#4395).
/// </summary>
/// <remarks>
/// Every Zarr backing store in the suite was a <c>Dictionary&lt;string, byte[]&gt;</c> behind
/// <see cref="ICloudRangeReader"/>, so nothing proved that the reader's ranged GETs, HEAD sizes and
/// not-found classification behave against an S3 implementation — the deployment shape the
/// cloud-native GA promise is about. This suite uploads a real Zarr v2 layout and drives the
/// production metadata extractor and subset reader over it.
/// <para>
/// Tier=Slow / Category=Emulator per ADR-0037: it runs in <c>nightly-slow-tier.yml</c>, not the
/// required PR gate, because it needs a container runtime.
/// </para>
/// </remarks>
[Collection("Emulators")]
public sealed class ZarrObjectStoreRangeTests
{
    private const string BucketEnv = "HONUA_TEST_S3_BUCKET";
    private const string RegionEnv = "HONUA_TEST_S3_REGION";
    private const string AccessKeyEnv = "HONUA_TEST_S3_ACCESS_KEY";
    private const string SecretKeyEnv = "HONUA_TEST_S3_SECRET_KEY";
    private const string ServiceUrlEnv = "HONUA_TEST_S3_SERVICE_URL";
    private const string ForcePathStyleEnv = "HONUA_TEST_S3_FORCE_PATH_STYLE";

    /// <summary>Edge length of the uploaded cube, in cells.</summary>
    private const int Grid = 8;

    /// <summary>Edge length of one chunk, in cells: the cube is a 2x2 grid of chunks.</summary>
    private const int Chunk = 4;

    /// <summary>The cube's cell value at storage row <paramref name="row"/>, column <paramref name="col"/>.</summary>
    /// <remarks>Asymmetric off the diagonal, so a transposed read is a different value grid.</remarks>
    private static float Sample(int row, int col) => (row * 10f) + col;

    /// <summary>
    /// The committed reader path, end to end over S3: scan, plan, ranged chunk fetch, decode.
    /// </summary>
    /// <remarks>
    /// The requested window is the cube's south-east quadrant, which lies wholly inside chunk
    /// (1, 1). Asserting the fetched key set is exactly that one chunk is what proves the read is
    /// chunk-addressed against the object store rather than pulling the whole array — the property
    /// that makes cloud-native serving viable at all.
    /// </remarks>
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

    /// <summary>
    /// The reader's range/size/not-found contract against a real S3 implementation.
    /// </summary>
    /// <remarks>
    /// <see cref="ZarrSubsetReader"/> sizes each chunk with a HEAD and then fetches it with a
    /// bounded ranged GET, so those three behaviours are the contract the cloud path depends on.
    /// The interior-offset case is the one an in-memory dictionary can never prove: it requires the
    /// service to honour a <c>Range</c> header and answer 206 with exactly that window.
    /// </remarks>
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
        // that to substitute the array's fill value for an unwritten chunk.
        var missing = async () => await reader.ReadRangeAsync(options.BucketName, key + ".absent", 0, 16);
        await missing.Should().ThrowAsync<FileNotFoundException>();
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

    /// <summary>Records which object keys a read path fetches, without altering the bytes returned.</summary>
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
