// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.TestKit.Formats;
using Xunit;

namespace Honua.Core.Tests.Raster.ZarrParser;

public class ZarrTileRendererTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private const int GridSize = 8;

    private const double SampleMin = 0;

    private const double SampleMax = 77;

    private static float Sample(int row, int col) => (row * 10f) + col;

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task Render_GeoreferencedSlice_ProducesGrayscalePixelsMatchingTheCellValues()
    {
        var (metadata, reader) = await BuildAsync();

        var bounds = new ZarrTileBounds(0, 0, GridSize, GridSize);
        ZarrTileSlicePlanner.TryPlan(metadata, null, bounds, null, null, 4 * 1024 * 1024, out var slice, out var error)
            .Should().BeTrue(error);

        // The fixture declares no ascending y axis, so storage row 0 is the northern edge and the
        // renderer must emit it as output row 0. This pins the premise the pixel grid below relies on.
        slice!.YAxisAscending.Should().BeFalse("the fixture stores rows north-to-south");

        var subset = await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", "tiles/render", metadata, slice.Plan.Request);

        var png = ZarrTileRenderer.Render(subset, slice, tileSize: 64);

        png.AsSpan(0, 8).ToArray().Should().Equal(PngSignature);
        ReadPngDimensions(png).Should().Be((64, 64));

        // Render at the grid's own resolution so nearest-neighbour sampling is the identity and
        // each output pixel is exactly one source cell.
        var exact = ZarrTileRenderer.Render(subset, slice, tileSize: GridSize);

        var image = AssertPixelGrid(
            exact,
            value => (ExpectedGray(value), ExpectedGray(value), ExpectedGray(value), (byte)255));

        // A blank tile is the failure this test exists to catch: the fixture's extreme cells must
        // survive the auto-ramp as pure black at the north-west corner and pure white at the south-east.
        image.Pixel(0, 0).Should().Be(((byte)0, (byte)0, (byte)0, (byte)255));
        image.Pixel(GridSize - 1, GridSize - 1).Should().Be(((byte)255, (byte)255, (byte)255, (byte)255));
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task Render_WithColormap_MapsValuesToColours()
    {
        var (metadata, reader) = await BuildAsync();

        var bounds = new ZarrTileBounds(0, 0, GridSize, GridSize);
        ZarrTileSlicePlanner.TryPlan(metadata, null, bounds, null, null, 4 * 1024 * 1024, out var slice, out _)
            .Should().BeTrue();
        var subset = await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", "tiles/render", metadata, slice!.Plan.Request);

        var colormap = new RasterColormap
        {
            Entries =
            [
                new RasterColormapEntry(SampleMin, 0, 0, 0, 255),
                new RasterColormapEntry(SampleMax, 255, 255, 255, 255),
            ],
        };

        var png = ZarrTileRenderer.Render(subset, slice, tileSize: GridSize, colormap: colormap);

        png.AsSpan(0, 8).ToArray().Should().Equal(PngSignature);

        // #4395: the colour mapping this test is named for was never checked — the assertion above
        // holds for any non-trivial PNG. The colormap linearly interpolates black at the fixture's
        // minimum to white at its maximum, so cell (r, c) must render as
        // round(Sample(r, c) / 77 * 255) in all three channels, fully opaque.
        AssertPixelGrid(
            png,
            value => (ExpectedGray(value), ExpectedGray(value), ExpectedGray(value), (byte)255));
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Render_BoxedFloatFill_IsTransparentAndExcludedFromTheRamp()
    {
        object fill = -9999f;
        (fill as double?).Should().BeNull();
        ZarrValueDecoder.TryGetFiniteFillValue(fill, out var converted).Should().BeTrue();
        converted.Should().Be(-9999d);

        var data = new byte[4 * sizeof(float)];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(0), 10f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), 20f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8), -9999f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(12), 30f);
        var subset = new ZarrSubsetResult("band", [2, 2], "<f4", data);
        var array = new ZarrArrayMetadata(
            "band",
            ZarrFormatVersion.V2,
            "band",
            [2, 2],
            [2, 2],
            "<f4",
            "C",
            null,
            fill,
            ["y", "x"]);
        var slice = new ZarrTileSlicePlan(
            new ZarrCoverageSubsetPlan(array, new ZarrSubsetRequest { Variable = "band", Start = [0, 0], Stop = [2, 2] }),
            YDimensionIndex: 0,
            XDimensionIndex: 1,
            GridXMin: 0,
            GridYMax: 2,
            CellWidth: 1,
            CellHeight: 1);

        var png = ZarrTileRenderer.Render(subset, slice, tileSize: 2, fillValue: converted);
        var image = MiniPngDecoder.Decode(png);

        image.Pixel(0, 0).Should().Be(((byte)0, (byte)0, (byte)0, (byte)255));
        image.Pixel(1, 0).Should().Be(((byte)128, (byte)128, (byte)128, (byte)255));
        image.Pixel(0, 1).Should().Be(((byte)0, (byte)0, (byte)0, (byte)0));
        image.Pixel(1, 1).Should().Be(((byte)255, (byte)255, (byte)255, (byte)255));
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Render_LossyInt64Fill_MasksOnlyTheExactSentinel()
    {
        const long sentinel = 9007199254740993L;
        const long neighbour = 9007199254740992L;
        object fill = sentinel;
        ZarrValueDecoder.TryGetFiniteFillValue(fill, out _).Should().BeFalse();

        var data = new byte[4 * sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(0), 10L);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), neighbour);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), sentinel);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(24), 30L);
        var subset = new ZarrSubsetResult("band", [2, 2], "<i8", data);
        var array = new ZarrArrayMetadata(
            "band",
            ZarrFormatVersion.V2,
            "band",
            [2, 2],
            [2, 2],
            "<i8",
            "C",
            null,
            fill,
            ["y", "x"]);
        var slice = new ZarrTileSlicePlan(
            new ZarrCoverageSubsetPlan(array, new ZarrSubsetRequest { Variable = "band", Start = [0, 0], Stop = [2, 2] }),
            YDimensionIndex: 0,
            XDimensionIndex: 1,
            GridXMin: 0,
            GridYMax: 2,
            CellWidth: 1,
            CellHeight: 1);

        var png = ZarrTileRenderer.RenderWithTypedFill(subset, slice, tileSize: 2, fillValue: fill);
        var image = MiniPngDecoder.Decode(png);

        image.Pixel(0, 0).Should().Be(((byte)0, (byte)0, (byte)0, (byte)255));
        image.Pixel(1, 0).Should().Be(((byte)255, (byte)255, (byte)255, (byte)255));
        image.Pixel(0, 1).Should().Be(((byte)0, (byte)0, (byte)0, (byte)0));
        image.Pixel(1, 1).Should().Be(((byte)0, (byte)0, (byte)0, (byte)255));
    }

    private static MiniPngDecoder.DecodedImage AssertPixelGrid(
        byte[] png,
        Func<float, (byte R, byte G, byte B, byte A)> expected)
    {
        var image = MiniPngDecoder.Decode(png);
        image.Width.Should().Be(GridSize);
        image.Height.Should().Be(GridSize);

        for (var row = 0; row < GridSize; row++)
        {
            for (var col = 0; col < GridSize; col++)
            {
                // North-up, non-ascending y: output pixel (x, y) renders storage cell (row y, col x).
                image.Pixel(col, row).Should().Be(
                    expected(Sample(row, col)),
                    "pixel ({0},{1}) renders storage cell (row {2}, col {3}) whose value is {4}",
                    col,
                    row,
                    row,
                    col,
                    Sample(row, col));
            }
        }

        return image;
    }

    private static byte ExpectedGray(double cellValue)
        => (byte)Math.Clamp((int)Math.Round((cellValue - SampleMin) / (SampleMax - SampleMin) * 255.0), 0, 255);

    private static async Task<(ZarrStoreMetadata Metadata, InMemoryZarrRangeReader Reader)> BuildAsync()
    {
        var objects = ZarrFixtureBuilder.BuildGroupedZlib(
            root: "tiles/render",
            rows: GridSize,
            cols: GridSize,
            chunkRows: 4,
            chunkCols: 4,
            sample: Sample,
            srid: 4326,
            xMin: 0,
            yMin: 0,
            xMax: GridSize,
            yMax: GridSize);
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "tiles/render");
        return (metadata, reader);
    }

    private static (int Width, int Height) ReadPngDimensions(byte[] png)
    {
        // IHDR data begins at byte 16 (8 signature + 4 length + 4 type).
        var width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        return (width, height);
    }
}
