// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.Core.Features.Shared.Models;
using Honua.TestKit.Formats;
using Xunit;

namespace Honua.Core.Tests.Raster.ZarrParser;

public sealed class ZarrRasterSliceReaderTests
{
    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadAsync_OversizedOutput_IsRejectedBeforeCatalogLookup()
    {
        var store = new CountingZarrStore([]);
        var reader = CreateReader(store);

        var result = await reader.ReadAsync(
            7,
            Request(outputWidth: 4097, outputHeight: 1));

        result.Status.Should().Be(ZarrRasterSliceReadStatus.InvalidSelection);
        result.Error.Should().Contain("output dimensions");
        store.ListCalls.Should().Be(0);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadAsync_DuplicateDimensions_AreRejectedBeforeCatalogLookup()
    {
        var store = new CountingZarrStore([]);
        var reader = CreateReader(store);
        var request = Request() with
        {
            Selections =
            [
                new ZarrPointSliceSelection("temperature", "elevation", 100),
                new ZarrPointSliceSelection("temperature", "ELEVATION", 200),
            ],
        };

        var result = await reader.ReadAsync(7, request);

        result.Status.Should().Be(ZarrRasterSliceReadStatus.InvalidSelection);
        result.Error.Should().Contain("only once");
        store.ListCalls.Should().Be(0);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadAsync_NonNativeSpatialReference_IsRejectedAfterOneCatalogLookup()
    {
        var store = new CountingZarrStore([Registration()]);
        var reader = CreateReader(store);

        var result = await reader.ReadAsync(7, Request() with { InputSrid = 3857 });

        result.Status.Should().Be(ZarrRasterSliceReadStatus.InvalidSelection);
        result.Error.Should().Contain("EPSG:4326");
        store.ListCalls.Should().Be(1);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadAsync_NonIntersectingBounds_ReturnsTypedOutsideCoverage()
    {
        var store = new CountingZarrStore([Registration()]);
        var reader = CreateReader(store);
        var request = Request() with
        {
            Bounds = new RasterExtent { XMin = 181, YMin = 0, XMax = 182, YMax = 1, Srid = 4326 },
        };

        var result = await reader.ReadAsync(7, request);

        result.Status.Should().Be(ZarrRasterSliceReadStatus.OutsideCoverage);
        store.ListCalls.Should().Be(1);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadAsync_AdvertisedStdZ_IsAcceptedAsTheVerticalAxis()
    {
        var reader = CreateReader(new CountingZarrStore([Registration()]));
        var request = Request() with
        {
            Selections = [new ZarrPointSliceSelection("temperature", "StdZ", 500)],
        };

        var result = await reader.ReadAsync(7, request);

        // The selection resolves (the empty range reader fails the read afterwards);
        // before the alias it was rejected as an unknown dimension.
        result.Status.Should().NotBe(ZarrRasterSliceReadStatus.InvalidSelection, result.Error);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadAsync_Wgs84ToWebMercator_UsesSeparable1DAxesWithoutTransformPointsCall()
    {
        var (store, rangeReader) = BuildPopulated4326Store();
        var reader = new ZarrRasterSliceReader(store, new ZarrSubsetReader(), [rangeReader]);
        var transform = new RecordingTransformService((x, y, from, to) =>
            from == 4326 && to == 3857
                ? WebMercatorMath.LonLatToWebMercator(x, y)
                : WebMercatorMath.WebMercatorToLonLat(x, y));

        var request = Request(outputWidth: 64, outputHeight: 64) with
        {
            Bounds = new RasterExtent { XMin = 0, YMin = 0, XMax = 40, YMax = 40, Srid = 4326 },
            InputSrid = 4326,
            OutputSrid = 3857,
        };

        var result = await reader.ReadAsync(7, request, transform);

        result.Status.Should().Be(ZarrRasterSliceReadStatus.Success, result.Error);
        result.Rgba.Should().HaveCount(64 * 64 * 4);
        transform.TransformPointsCalls.Should().Be(0, "4326<->3857 must use separable 1D axis transforms instead of a 2D TransformPointsAsync batch");
        result.Rgba![3].Should().Be(255);
        result.Rgba[^1].Should().Be(255);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadAsync_GeneralCrsLargeOutput_UsesBounded33x33ControlGrid()
    {
        var (store, rangeReader) = BuildPopulated4326Store();
        var reader = new ZarrRasterSliceReader(store, new ZarrSubsetReader(), [rangeReader]);
        // Simple linear synthetic CRS 32610 = 1000 * lon/lat so bilinear interpolation is exact.
        var transform = new RecordingTransformService((x, y, from, to) =>
            from == 4326 && to == 32610 ? (x * 1000.0, y * 1000.0) : (x / 1000.0, y / 1000.0));

        var request = Request(outputWidth: 128, outputHeight: 128) with
        {
            Bounds = new RasterExtent { XMin = 0, YMin = 0, XMax = 40_000, YMax = 40_000, Srid = 32610 },
            InputSrid = 32610,
            OutputSrid = 32610,
        };

        var result = await reader.ReadAsync(7, request, transform);

        result.Status.Should().Be(ZarrRasterSliceReadStatus.Success, result.Error);
        transform.TransformPointsCalls.Should().Be(1);
        transform.LastPointCount.Should().Be(33 * 33, "general CRS reprojection must transform a bounded 33x33 control grid, not 128x128");
        result.Rgba![0].Should().Be(0, "north-west pixel holds the minimum cell value");
        result.Rgba![((127 * 128) + 127) * 4].Should().Be(255, "south-east pixel holds the maximum cell value");
    }

    private static (CountingZarrStore Store, InMemoryZarrRangeReader RangeReader) BuildPopulated4326Store()
    {
        var chunkBytes = new byte[4 * 4 * 4 * sizeof(float)];
        for (var e = 0; e < 4; e++)
        {
            for (var r = 0; r < 4; r++)
            {
                for (var c = 0; c < 4; c++)
                {
                    var idx = ((e * 16) + (r * 4) + c) * sizeof(float);
                    BitConverter.GetBytes((float)((r * 10) + c)).CopyTo(chunkBytes, idx);
                }
            }
        }

        var registration = Registration() with
        {
            Metadata = Registration().Metadata! with
            {
                Extent = new RasterExtent { XMin = 0, YMin = 0, XMax = 40, YMax = 40, Srid = 4326 },
            },
        };
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["vertical/temperature/0.0.0"] = chunkBytes,
        };
        return (new CountingZarrStore([registration]), new InMemoryZarrRangeReader(objects));
    }

    private sealed class RecordingTransformService(Func<double, double, int, int, (double X, double Y)> project) : ICoordinateTransformService
    {
        public int TransformPointsCalls { get; private set; }

        public int LastPointCount { get; private set; }

        public ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
            double minX, double minY, double maxX, double maxY, int fromSrid, int toSrid, CancellationToken cancellationToken = default)
        {
            var a = project(minX, minY, fromSrid, toSrid);
            var b = project(maxX, maxY, fromSrid, toSrid);
            return ValueTask.FromResult<(double, double, double, double)?>((Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
        }

        public ValueTask<(double X, double Y)?> TransformPointAsync(
            double x, double y, int fromSrid, int toSrid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<(double, double)?>(project(x, y, fromSrid, toSrid));

        public ValueTask<bool> TransformPointsAsync(
            double[] xs, double[] ys, int fromSrid, int toSrid, CancellationToken cancellationToken = default)
        {
            TransformPointsCalls++;
            LastPointCount = xs.Length;
            for (var i = 0; i < xs.Length; i++)
            {
                (xs[i], ys[i]) = project(xs[i], ys[i], fromSrid, toSrid);
            }
            return ValueTask.FromResult(true);
        }
    }

    private static ZarrRasterSliceReader CreateReader(IZarrStore store)
        => new(
            store,
            new ZarrSubsetReader(),
            [new InMemoryZarrRangeReader(new Dictionary<string, byte[]>())]);

    private static ZarrRasterSliceReadRequest Request(int outputWidth = 16, int outputHeight = 16)
        => new(
            Bounds: null,
            InputSrid: 4326,
            OutputWidth: outputWidth,
            OutputHeight: outputHeight,
            Selections: [new ZarrPointSliceSelection("temperature", "elevation", 500)]);

    private static ZarrRegistration Registration()
        => new()
        {
            Id = 1,
            LayerId = 7,
            Name = "vertical",
            Provider = CloudStorageProvider.AwsS3,
            Bucket = "bucket",
            RootPath = "vertical",
            CreatedAt = DateTimeOffset.UnixEpoch,
            Metadata = new ZarrStoreMetadata(
                ZarrFormatVersion.V2,
                4326,
                new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 },
                [
                    new ZarrArrayMetadata(
                        "temperature",
                        ZarrFormatVersion.V2,
                        "temperature",
                        [4, 4, 4],
                        [4, 4, 4],
                        "<f4",
                        "C",
                        null,
                        null,
                        ["elevation", "y", "x"]),
                ],
                "temperature",
                "x",
                "y",
                null,
                Axes: [new ZarrAxis("elevation", 4, null, 0, 1000)]),
        };

    private sealed class CountingZarrStore(ZarrRegistration[] registrations) : IZarrStore
    {
        public int ListCalls { get; private set; }

        public Task<ZarrRegistration[]> ListByLayerAsync(
            int layerId,
            CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult(registrations);
        }

        public Task<ZarrRegistration?> GetAsync(long id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ZarrRegistration> RegisterAsync(
            ZarrRegistrationRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> UnregisterAsync(long id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task UpdateMetadataAsync(
            long id,
            ZarrStoreMetadata metadata,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
