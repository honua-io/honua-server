// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.TestKit.Formats;
using NSubstitute;
using Xunit;

namespace Honua.Core.Tests.Raster.ZarrParser;

public sealed class ZarrTypedNoDataConsumerTests
{
    // Honua renders declared fill sentinels as nodata. Compare in the source dtype:
    // both the exact and inexact sentinel cases have a distinct adjacent real sample
    // that collapses onto the same double. Binary subset values must stay unchanged.
    [Theory]
    [InlineData("<i8", 0)]
    [InlineData("<i8", 1)]
    [InlineData("<u8", 0)]
    [InlineData("<u8", 1)]
    [Trait("Tier", "Fast")]
    public async Task Render_Int64Fill_PreservesAdjacentValidPixel(string dtype, int offset)
    {
        var (registration, objects, fill) = Fixture(dtype, offset);
        var array = registration.Metadata!.Arrays[0];
        var metadata = registration.Metadata;
        var ranges = new InMemoryZarrRangeReader(objects);
        var subset = await new ZarrSubsetReader().ReadSubsetAsync(ranges, "bucket", "typed", metadata,
            new ZarrSubsetRequest { Variable = "band", Start = [0, 0, 0], Stop = [1, 2, 2] });
        subset.Data.Should().Equal(objects["typed/band/0.0.0"]);
        var slice = new ZarrTileSlicePlan(new ZarrCoverageSubsetPlan(array,
            new ZarrSubsetRequest { Variable = "band", Start = [0, 0, 0], Stop = [1, 2, 2] }),
            YDimensionIndex: 1, XDimensionIndex: 2, GridXMin: 0, GridYMax: 2, CellWidth: 1, CellHeight: 1);
        var image = MiniPngDecoder.Decode(ZarrTileRenderer.RenderWithTypedFill(subset, slice, 2, fillValue: fill));
        image.Pixel(0, 0).Item4.Should().Be(0, "the exact typed sentinel is transparent");
        image.Pixel(1, 0).Item4.Should().Be(255, "its distinct adjacent integer is real data");

    }

    [Theory]
    [InlineData("<i8", 0)]
    [InlineData("<i8", 1)]
    [InlineData("<u8", 0)]
    [InlineData("<u8", 1)]
    [Trait("Tier", "Fast")]
    public async Task ReadPoint_Int64Fill_ReturnsNullOnlyForTypedSentinel(string dtype, int offset)
    {
        var (registration, objects, _) = Fixture(dtype, offset);
        var store = Substitute.For<IZarrStore>();
        store.ListByLayerAsync(7, Arg.Any<CancellationToken>()).Returns(Task.FromResult(new[] { registration }));
        var reader = new ZarrPointSliceReader(store, new ZarrSubsetReader(), [new InMemoryZarrRangeReader(objects)]);
        var selections = new[] { new ZarrPointSliceSelection("band", "elevation", 500) };
        var missing = await reader.ReadAsync(7, 0.5, 1.5, 4326, selections);
        missing.Status.Should().Be(ZarrPointSliceReadStatus.Success, missing.Error);
        missing.Value.Should().BeNull("canonical raster nodata is a null sample, never a JSON NaN");
        var valid = await reader.ReadAsync(7, 1.5, 1.5, 4326, selections);
        valid.Status.Should().Be(ZarrPointSliceReadStatus.Success, valid.Error);
        valid.Value.Should().NotBeNull("the adjacent source integer is not the sentinel");
        double.IsFinite(valid.Value!.Value).Should().BeTrue();
    }

    [Theory]
    [InlineData("<i8", 0)]
    [InlineData("<i8", 1)]
    [InlineData("<u8", 0)]
    [InlineData("<u8", 1)]
    [Trait("Tier", "Fast")]
    public async Task ReadSlice_Int64Fill_PreservesAdjacentValidPixel(string dtype, int offset)
    {
        var (registration, objects, _) = Fixture(dtype, offset);
        var ranges = new InMemoryZarrRangeReader(objects);
        var store = Substitute.For<IZarrStore>();
        store.ListByLayerAsync(7, Arg.Any<CancellationToken>()).Returns(Task.FromResult(new[] { registration }));
        var reader = new ZarrRasterSliceReader(store, new ZarrSubsetReader(), [ranges]);
        var rendered = await reader.ReadAsync(7, new ZarrRasterSliceReadRequest(null, 4326, 2, 2,
            [new ZarrPointSliceSelection("band", "elevation", 500)]));
        rendered.Status.Should().Be(ZarrRasterSliceReadStatus.Success, rendered.Error);
        rendered.Rgba![3].Should().Be(0);
        rendered.Rgba[7].Should().Be(255, "slice reprojection/render must retain the adjacent real sample");
    }

    [Theory]
    [InlineData("<i8")]
    [InlineData("<u8")]
    [Trait("Tier", "Fast")]
    public async Task Render_ExplicitNullAndOverride_PreserveCallerNodataPolicy(string dtype)
    {
        var (registration, objects, _) = Fixture(dtype, 0);
        var metadata = registration.Metadata!;
        var request = new ZarrSubsetRequest { Variable = "band", Start = [0, 0, 0], Stop = [1, 2, 2] };
        var subset = await new ZarrSubsetReader().ReadSubsetAsync(new InMemoryZarrRangeReader(objects),
            "bucket", "typed", metadata, request);
        var slice = new ZarrTileSlicePlan(new ZarrCoverageSubsetPlan(metadata.Arrays[0], request),
            YDimensionIndex: 1, XDimensionIndex: 2, GridXMin: 0, GridYMax: 2, CellWidth: 1, CellHeight: 1);
        var optOut = MiniPngDecoder.Decode(ZarrTileRenderer.Render(subset, slice, 2, fillValue: null));
        optOut.Pixel(0, 0).Item4.Should().Be(255, "null opts out of finite nodata masking despite array metadata");
        var overridden = MiniPngDecoder.Decode(ZarrTileRenderer.Render(subset, slice, 2, fillValue: 10d));
        overridden.Pixel(0, 0).Item4.Should().Be(255, "the metadata sentinel must not override an explicit caller policy");
        overridden.Pixel(1, 1).Item4.Should().Be(0, "the explicit override is the selected sentinel");
    }

    [Theory]
    [InlineData(float.NaN, false)]
    [InlineData(float.PositiveInfinity, false)]
    [InlineData(float.NegativeInfinity, false)]
    [InlineData(float.NaN, true)]
    [Trait("Tier", "Fast")]
    public async Task ReadPoint_NonFiniteOrAbsentNanSample_ReturnsNull(float sample, bool missing)
    {
        var (registration, objects, _) = Fixture("<i8", 0);
        var metadata = registration.Metadata!;
        var array = metadata.Arrays[0] with { DataType = "<f4", FillValue = float.NaN };
        registration = registration with { Metadata = metadata with { Arrays = [array] } };
        var bytes = new byte[16];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, sample);
        for (var index = 1; index < 4; index++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * 4), 10f);
        objects["typed/band/0.0.0"] = bytes;
        if (missing) objects.Clear();
        var store = Substitute.For<IZarrStore>();
        store.ListByLayerAsync(7, Arg.Any<CancellationToken>()).Returns(Task.FromResult(new[] { registration }));
        var reader = new ZarrPointSliceReader(store, new ZarrSubsetReader(), [new InMemoryZarrRangeReader(objects)]);
        var result = await reader.ReadAsync(7, 0.5, 1.5, 4326,
            [new ZarrPointSliceSelection("band", "elevation", 500)]);
        result.Status.Should().Be(ZarrPointSliceReadStatus.Success, result.Error);
        result.Value.Should().BeNull("canonical point nodata must not leak a non-finite JSON number");
    }

    private static (ZarrRegistration Registration, Dictionary<string, byte[]> Objects, object Fill) Fixture(string dtype, int offset)
    {
        const long adjacent = 9007199254740992L;
        object fill = dtype == "<i8" ? (object)(adjacent + offset) : (ulong)(adjacent + offset);
        var bytes = new byte[32];
        var values = new[] { adjacent + offset, adjacent + (1 - offset), 1L, 10L };
        for (var index = 0; index < values.Length; index++)
        {
            if (dtype == "<i8") BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(index * 8), values[index]);
            else BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(index * 8), (ulong)values[index]);
        }
        var array = new ZarrArrayMetadata("band", ZarrFormatVersion.V2, "band", [1, 2, 2], [1, 2, 2],
            dtype, "C", null, fill, ["elevation", "y", "x"]);
        var metadata = new ZarrStoreMetadata(ZarrFormatVersion.V2, 4326,
            new RasterExtent { XMin = 0, YMin = 0, XMax = 2, YMax = 2, Srid = 4326 },
            [array], "band", "x", "y", null, Axes: [new ZarrAxis("elevation", 1, null, 500, 500)]);
        var registration = new ZarrRegistration
        {
            Id = 1, LayerId = 7, Name = "typed", Provider = CloudStorageProvider.AwsS3,
            Bucket = "bucket", RootPath = "typed", CreatedAt = DateTimeOffset.UnixEpoch, Metadata = metadata
        };
        return (registration, new Dictionary<string, byte[]> { ["typed/band/0.0.0"] = bytes }, fill);
    }
}
