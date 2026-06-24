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

    [Fact]
    public async Task Render_GeoreferencedSlice_ProducesValidPng()
    {
        var (metadata, reader) = await BuildAsync();

        var bounds = new ZarrTileBounds(0, 0, 8, 8);
        ZarrTileSlicePlanner.TryPlan(metadata, null, bounds, null, null, 4 * 1024 * 1024, out var slice, out var error)
            .Should().BeTrue(error);

        var subset = await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", "tiles/render", metadata, slice!.Plan.Request);

        var png = ZarrTileRenderer.Render(subset, slice, tileSize: 64);

        png.AsSpan(0, 8).ToArray().Should().Equal(PngSignature);
        ReadPngDimensions(png).Should().Be((64, 64));
    }

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
