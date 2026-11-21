// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.TestKit.Formats;
using static Honua.Core.Tests.Raster.CogParser.TiffTagReader;

namespace Honua.Core.Tests.Raster.ZarrParser;

public sealed class ZarrGeoTiffRendererTests
{
    private const int GridSize = 8;

    private static float Sample(int row, int col) => (row * 10f) + col;

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task Render_NativeGrid_KeepsFloatSamplesNoDataAndGeographicGeoreferencing()
    {
        var extent = Extent(0, GridSize);
        var tiff = await RenderAsync(extent, GridSize, GridSize);

        var fields = ReadFields(tiff);
        U32(fields[256]).Should().Be(GridSize);
        U32(fields[257]).Should().Be(GridSize);
        U16(fields[258]).Should().Be(32);
        U16(fields[339]).Should().Be(3, "float32 samples are written as IEEE floating point");
        Encoding.ASCII.GetString(fields[42113]).Should().Be("nan\0", "the fixture's NaN fill is the nodata value");
        BinaryPrimitives.ReadDoubleLittleEndian(fields[33550]).Should().Be(1);
        BinaryPrimitives.ReadDoubleLittleEndian(fields[33922].AsSpan(24)).Should().Be(0);
        BinaryPrimitives.ReadDoubleLittleEndian(fields[33922].AsSpan(32)).Should().Be(GridSize);
        U16(fields[34735].AsSpan(14)).Should().Be(2, "EPSG:4326 is a geographic model");
        U16(fields[34735].AsSpan(24)).Should().Be(2048);
        U16(fields[34735].AsSpan(30)).Should().Be(4326);

        var pixels = ReadPixels(tiff, (GridSize, GridSize));
        for (var row = 0; row < GridSize; row++)
        {
            for (var col = 0; col < GridSize; col++)
            {
                pixels[row, col].Should().Be(Sample(row, col), "output row {0} is storage row {0} (north up)", row);
            }
        }
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task Render_WindowBeyondTheExtent_FillsUncoveredPixelsWithNoData()
    {
        var tiff = await RenderAsync(Extent(-GridSize, GridSize), GridSize * 2, GridSize);

        var pixels = ReadPixels(tiff, (GridSize * 2, GridSize));
        for (var row = 0; row < GridSize; row++)
        {
            for (var col = 0; col < GridSize * 2; col++)
            {
                var expected = col < GridSize ? float.NaN : Sample(row, col - GridSize);
                Describe(pixels[row, col]).Should().Be(
                    Describe(expected), "cell ({0},{1}); cells west of the coverage are nodata", row, col);
            }
        }
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task Render_DownscaledGrid_TakesTheNearestSourceCellPerPixelCentre()
    {
        var tiff = await RenderAsync(Extent(0, GridSize), GridSize / 2, GridSize / 2);

        var pixels = ReadPixels(tiff, (GridSize / 2, GridSize / 2));
        for (var row = 0; row < GridSize / 2; row++)
        {
            for (var col = 0; col < GridSize / 2; col++)
            {
                pixels[row, col].Should().Be(Sample((row * 2) + 1, (col * 2) + 1));
            }
        }
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Encode_ProjectedIntegerImage_WritesProjectedGeoKeysAndRejectsMismatchedBuffers()
    {
        var image = new GeoTiffStripImage
        {
            LittleEndianSamples = [1, 0, 2, 0, 3, 0, 4, 0],
            Width = 2,
            Height = 2,
            BitsPerSample = 16,
            SampleFormat = 2,
            Extent = new RasterExtent { XMin = 100, YMin = 200, XMax = 120, YMax = 240, Srid = 3857 },
        };

        var fields = ReadFields(GeoTiffStripEncoder.Encode(image));
        U16(fields[339]).Should().Be(2);
        U16(fields[34735].AsSpan(14)).Should().Be(1, "EPSG:3857 is a projected model");
        U16(fields[34735].AsSpan(24)).Should().Be(3072);
        U16(fields[34735].AsSpan(30)).Should().Be(3857);
        fields.Should().NotContainKey(42113, "no nodata value was declared");
        BinaryPrimitives.ReadDoubleLittleEndian(fields[33550].AsSpan(8)).Should().Be(20);

        var truncated = () => GeoTiffStripEncoder.Encode(image with { LittleEndianSamples = [1, 0] });
        truncated.Should().Throw<ArgumentException>();
    }

    private static string Describe(float value)
        => float.IsNaN(value) ? "NaN" : value.ToString("R", CultureInfo.InvariantCulture);

    private static RasterExtent Extent(double xMin, double xMax)
        => new() { XMin = xMin, YMin = 0, XMax = xMax, YMax = GridSize, Srid = 4326 };

    private static async Task<byte[]> RenderAsync(RasterExtent extent, int width, int height)
    {
        var objects = ZarrFixtureBuilder.BuildGroupedZlib(
            "geotiff/render", GridSize, GridSize, 4, 4, Sample, 4326, 0, 0, GridSize, GridSize);
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "geotiff/render");
        var bounds = new ZarrTileBounds(extent.XMin, extent.YMin, extent.XMax, extent.YMax);
        ZarrTileSlicePlanner.TryPlan(metadata, null, bounds, null, null, 4 * 1024 * 1024, out var slice, out var error)
            .Should().BeTrue(error);
        var subset = await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", "geotiff/render", metadata, slice!.Plan.Request);
        return ZarrGeoTiffRenderer.Render(subset, slice, new ZarrGeoTiffOutput { Extent = extent, Width = width, Height = height });
    }

    private static float[,] ReadPixels(byte[] tiff, (int Width, int Height) size)
    {
        var (width, height) = size;
        var fields = ReadFields(tiff);
        var offset = (int)U32(fields[273]);
        U32(fields[279]).Should().Be((uint)(width * height * sizeof(float)));
        var pixels = new float[height, width];
        for (var index = 0; index < width * height; index++)
        {
            pixels[index / width, index % width] = BinaryPrimitives.ReadSingleLittleEndian(tiff.AsSpan(offset + (index * sizeof(float))));
        }

        return pixels;
    }
}
