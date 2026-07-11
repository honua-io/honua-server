// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Xunit;

namespace Honua.Core.Tests.Raster.ZarrParser;

public sealed class ZarrRasterSliceReaderTests
{
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
