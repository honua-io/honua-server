// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Xunit;

namespace Honua.Core.Tests.Raster.ZarrParser;

public class ZarrTileRendererTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// The rendered tile's pixels, not just its framing (honua-server#4395).
    /// </summary>
    /// <remarks>
    /// This test asserted the PNG signature and the IHDR dimensions, so a renderer that emitted a
    /// correctly-sized blank or garbled tile passed. The fixture's cell value is
    /// <c>sample(r, c) = r + c</c> over an 8x8 grid, and the no-colormap path auto-ramps
    /// grayscale over the slice's finite range — so every pixel's grey level is computable from
    /// the fixture: <c>round((r + c) / 14 * 255)</c>.
    /// </remarks>
    [Fact]
    public async Task Render_GeoreferencedSlice_ProducesGrayscalePixelsMatchingTheCellValues()
    {
        var (metadata, reader) = await BuildAsync();

        var bounds = new ZarrTileBounds(0, 0, 8, 8);
        ZarrTileSlicePlanner.TryPlan(metadata, null, bounds, null, null, 4 * 1024 * 1024, out var slice, out var error)
            .Should().BeTrue(error);

        var subset = await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", "tiles/render", metadata, slice!.Plan.Request);

        var png = ZarrTileRenderer.Render(subset, slice, tileSize: 64);

        png.AsSpan(0, 8).ToArray().Should().Equal(PngSignature);
        ReadPngDimensions(png).Should().Be((64, 64));

        // Render at the grid's own resolution so nearest-neighbour sampling is the identity and
        // each output pixel is exactly one source cell.
        var exact = ZarrTileRenderer.Render(subset, slice, tileSize: 8);
        var image = MiniPngDecoder.Decode(exact);
        image.Width.Should().Be(8);
        image.Height.Should().Be(8);

        var levels = MiniPngDecoder.GrayLevels(image);

        // Expected multiset, computed from the fixture rather than snapshotted from the output.
        var expected = new List<byte>();
        for (var r = 0; r < 8; r++)
        {
            for (var c = 0; c < 8; c++)
            {
                expected.Add(ExpectedGray(r + c));
            }
        }

        levels.Should().BeEquivalentTo(expected,
            "every rendered pixel must carry the grey level its source cell value maps to");

        // Corner anchors. The anti-diagonal corners both come from cells with value 7 regardless
        // of the Y-axis orientation, and the main-diagonal corners are the extremes.
        image.Pixel(7, 0).Should().Be((ExpectedGray(7), ExpectedGray(7), ExpectedGray(7), (byte)255));
        image.Pixel(0, 7).Should().Be((ExpectedGray(7), ExpectedGray(7), ExpectedGray(7), (byte)255));
        new[] { image.Pixel(0, 0).R, image.Pixel(7, 7).R }.Should().BeEquivalentTo(
            new byte[] { 0, 255 },
            "the value-0 and value-14 corners render as pure black and pure white");

        // A blank tile is the failure this test exists to catch.
        levels.Should().Contain((byte)0).And.Contain((byte)255);
    }

    /// <summary>Grayscale auto-ramp over the fixture's 0..14 value range.</summary>
    private static byte ExpectedGray(int cellValue)
        => (byte)Math.Clamp((int)Math.Round(cellValue / 14.0 * 255.0), 0, 255);

    [Fact]
    public async Task Render_WithColormap_MapsValuesToColours()
    {
        var (metadata, reader) = await BuildAsync();

        var bounds = new ZarrTileBounds(0, 0, 8, 8);
        ZarrTileSlicePlanner.TryPlan(metadata, null, bounds, null, null, 4 * 1024 * 1024, out var slice, out _)
            .Should().BeTrue();
        var subset = await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", "tiles/render", metadata, slice!.Plan.Request);

        var colormap = new RasterColormap
        {
            Entries =
            [
                new RasterColormapEntry(0, 0, 0, 0, 255),
                new RasterColormapEntry(14, 255, 255, 255, 255),
            ],
        };

        var png = ZarrTileRenderer.Render(subset, slice, tileSize: 8, colormap: colormap);

        png.AsSpan(0, 8).ToArray().Should().Equal(PngSignature);
        png.Length.Should().BeGreaterThan(8);

        // #4395: the colour mapping this test is named for was never checked — the assertions
        // above hold for any non-trivial PNG. The colormap linearly interpolates black at value 0
        // to white at value 14, so cell (r, c) must render as
        // round((r + c) / 14 * 255) in all three channels, fully opaque.
        var image = MiniPngDecoder.Decode(png);
        image.Width.Should().Be(8);
        image.Height.Should().Be(8);

        var expected = new List<byte>();
        for (var r = 0; r < 8; r++)
        {
            for (var c = 0; c < 8; c++)
            {
                expected.Add(ExpectedGray(r + c));
            }
        }

        MiniPngDecoder.GrayLevels(image).Should().BeEquivalentTo(expected,
            "each cell value must map through the two-stop colormap to its interpolated colour");

        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                image.Pixel(x, y).A.Should().Be(255,
                    "both colormap stops declare alpha 255, so no pixel may render transparent");
            }
        }
    }

    private static async Task<(ZarrStoreMetadata Metadata, InMemoryZarrRangeReader Reader)> BuildAsync()
    {
        var objects = ZarrFixtureBuilder.BuildGroupedZlib(
            root: "tiles/render",
            rows: 8,
            cols: 8,
            chunkRows: 4,
            chunkCols: 4,
            sample: (r, c) => r + c,
            srid: 4326,
            xMin: 0,
            yMin: 0,
            xMax: 8,
            yMax: 8);
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
