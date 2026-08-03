// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.CogParser;

namespace Honua.Benchmarks.RasterStorage;

internal sealed record CogRasterStorageBenchmarkOptions(
    Uri ObjectUri,
    string FixtureId,
    int WarmupCount,
    int SampleCount);

internal sealed class CogRasterStorageBenchmarkAdapter(CogRasterStorageBenchmarkOptions options)
{
    private readonly RasterStorageProtocolDefinition _protocol = RasterStorageProtocol.Create();

    public async Task<RasterStorageBenchmarkRun> RunAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var reader = new CountingHttpRangeReader(httpClient, options.ObjectUri);
        var metadata = await new CogMetadataExtractor()
            .ReadMetadataAsync(reader, string.Empty, options.ObjectUri.AbsoluteUri, cancellationToken)
            .ConfigureAwait(false);
        var fixture = _protocol.Fixtures.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, options.FixtureId, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Unknown raster fixture '{options.FixtureId}'.");
        ValidateFixture(
            metadata.Width,
            metadata.Height,
            metadata.BandCount,
            metadata.BitsPerSample,
            metadata.PixelType,
            fixture);

        var baseLevel = metadata.OverviewLevels.FirstOrDefault()
            ?? throw new InvalidDataException("COG metadata contains no image levels.");
        if (baseLevel.TileOffsets.Length == 0 || baseLevel.TileOffsets.Length != baseLevel.TileByteCounts.Length)
        {
            throw new InvalidDataException("COG base level has no valid tile offset table.");
        }

        var tileIndex = baseLevel.TileOffsets.Length / 2;
        var layout = new TilePixelLayout(
            metadata.TileWidth,
            metadata.BandCount,
            metadata.BitsPerSample,
            metadata.Predictor,
            metadata.IsLittleEndian);
        var expectedTileBytes = checked(metadata.TileWidth * metadata.TileHeight * metadata.BandCount * Math.Max(1, metadata.BitsPerSample / 8));

        for (var iteration = 0; iteration < options.WarmupCount; iteration++)
        {
            await ReadAndDecodeTileAsync(reader, metadata.Compression, layout, expectedTileBytes, baseLevel, tileIndex, cancellationToken)
                .ConfigureAwait(false);
        }

        reader.ResetCounters();
        var samples = new List<double>(options.SampleCount);
        for (var iteration = 0; iteration < options.SampleCount; iteration++)
        {
            var stopwatch = Stopwatch.StartNew();
            await ReadAndDecodeTileAsync(reader, metadata.Compression, layout, expectedTileBytes, baseLevel, tileIndex, cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        var objectSize = await reader.GetObjectSizeAsync(string.Empty, options.ObjectUri.AbsoluteUri, cancellationToken)
            .ConfigureAwait(false);
        var result = RasterStorageStatistics.CreateCompletedResult(
            RasterStorageLayout.ObjectCog,
            fixture.Id,
            RasterStorageWorkload.Tile,
            options.WarmupCount,
            samples,
            RasterStorageMetrics.ForCog(
                (double)reader.RequestCount / options.SampleCount,
                (double)reader.BytesRead / options.SampleCount,
                fixture.LogicalBytes,
                objectSize));
        var run = new RasterStorageBenchmarkRun(
            RasterStorageProtocol.Version,
            Guid.NewGuid().ToString("N"),
            startedAt,
            DateTimeOffset.UtcNow,
            new RasterStorageEnvironment(
                Environment.OSVersion.ToString(),
                Environment.Version.ToString(),
                Environment.ProcessorCount,
                Environment.GetEnvironmentVariable("GITHUB_SHA"),
                null,
                null,
                options.ObjectUri.Scheme,
                "Signed object URI is intentionally omitted; metadata scan is warm and tile samples include one range read plus managed decode."),
            [result]);
        RasterStorageProtocolValidator.ValidateRun(_protocol, run);
        return run;
    }

    private static async Task ReadAndDecodeTileAsync(
        CountingHttpRangeReader reader,
        string compression,
        TilePixelLayout layout,
        int expectedTileBytes,
        Honua.Core.Features.Raster.Domain.CogOverviewLevel level,
        int tileIndex,
        CancellationToken cancellationToken)
    {
        var compressed = await reader.ReadRangeAsync(
                string.Empty,
                string.Empty,
                level.TileOffsets[tileIndex],
                level.TileByteCounts[tileIndex],
                cancellationToken)
            .ConfigureAwait(false);
        _ = TileDecompressor.Decompress(compressed, compression, layout, expectedTileBytes);
    }

    private static void ValidateFixture(
        int width,
        int height,
        int bandCount,
        int bitsPerSample,
        string pixelType,
        RasterFixtureDefinition fixture)
    {
        if (fixture.Scenes.Count != 1)
        {
            throw new InvalidDataException(
                $"COG range adapter accepts one object; fixture '{fixture.Id}' contains {fixture.Scenes.Count} scenes.");
        }

        var scene = fixture.Scenes[0];
        if (scene.Width != width || scene.Height != height || fixture.BandCount != bandCount)
        {
            throw new InvalidDataException(
                $"COG is {width}x{height}x{bandCount}, but fixture '{fixture.Id}' requires {scene.Width}x{scene.Height}x{fixture.BandCount}.");
        }

        if (fixture.PixelType != "8BUI" || bitsPerSample != 8 || pixelType != "uint8")
        {
            throw new InvalidDataException(
                $"COG pixel type {pixelType}/{bitsPerSample}-bit does not match fixture '{fixture.Id}' type {fixture.PixelType}.");
        }
    }
}

internal sealed class CountingHttpRangeReader(HttpClient httpClient, Uri objectUri) : ICloudRangeReader
{
    private long? _objectSize;

    public CloudStorageProvider Provider => CloudStorageProvider.Local;

    public long RequestCount { get; private set; }

    public long BytesRead { get; private set; }

    public async Task<byte[]> ReadRangeAsync(
        string bucket,
        string key,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0 || length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Range offset must be non-negative and length must be positive.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, objectUri);
        request.Headers.Range = new RangeHeaderValue(offset, checked(offset + length - 1));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new InvalidDataException(
                $"Object endpoint returned {(int)response.StatusCode}; a byte-range benchmark requires HTTP 206.");
        }

        if (response.Content.Headers.ContentRange?.Length is { } totalLength)
        {
            _objectSize = totalLength;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        RequestCount++;
        BytesRead += bytes.LongLength;
        return bytes;
    }

    public async Task<Stream> ReadRangeStreamAsync(
        string bucket,
        string key,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
        => new MemoryStream(
            await ReadRangeAsync(bucket, key, offset, length, cancellationToken).ConfigureAwait(false),
            writable: false);

    public async Task<long> GetObjectSizeAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken = default)
    {
        if (_objectSize is { } cached)
        {
            return cached;
        }

        _ = await ReadRangeAsync(bucket, key, 0, 1, cancellationToken).ConfigureAwait(false);
        return _objectSize ?? throw new InvalidDataException("Object response did not include a total Content-Range length.");
    }

    public void ResetCounters()
    {
        RequestCount = 0;
        BytesRead = 0;
    }
}
