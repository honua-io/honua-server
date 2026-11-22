// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.Exports;
using Honua.TestKit.Attributes;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Admin;

public sealed class ExportThumbnailTests
{
    private static readonly double[] Bounds = [-157.9, 21.3, -157.8, 21.4];

    [UnitTheory]
    [InlineData(null, "#CAD9E9")] // the default fill, 25% #2D69A5 over white
    [InlineData("{\"version\":8,\"layers\":[{\"id\":\"f\",\"type\":\"fill\",\"paint\":{\"fill-color\":\"#FF0000\",\"fill-opacity\":1}}]}", "#FF0000")]
    public void Render_PolygonOverItsExtent_DrawsTheLayerStyleAtTheCentre(string? style, string expectedHex)
    {
        var png = ExportThumbnail.Render(new ExportThumbnailSource([Square()], Bounds, MetadataV2GeometryType.Polygon), style);

        using var bitmap = SKBitmap.Decode(png);
        bitmap.Width.Should().Be(ExportThumbnail.Size);
        bitmap.Height.Should().Be(ExportThumbnail.Size);
        var centre = bitmap.GetPixel(ExportThumbnail.Size / 2, ExportThumbnail.Size / 2);
        centre.Should().NotBe(SKColors.White, "the polygon covers the centre of its own extent");
        var expected = SKColor.Parse(expectedHex);
        Math.Abs(centre.Red - expected.Red).Should().BeLessThan(0x80);
        Math.Abs(centre.Green - expected.Green).Should().BeLessThan(0x80);
        Math.Abs(centre.Blue - expected.Blue).Should().BeLessThan(0x80);
        bitmap.GetPixel(2, 2).Should().Be(SKColors.White, "the padding around the extent stays empty");
    }

    [UnitTest]
    public void SquareMercatorExtent_SinglePoint_KeepsAMinimumSpan()
    {
        var extent = ExportThumbnail.SquareMercatorExtent([-157.85, 21.35, -157.85, 21.35]);

        (extent.MaxX - extent.MinX).Should().BeApproximately(1000, 0.001);
        (extent.MaxY - extent.MinY).Should().BeApproximately(1000, 0.001);
    }

    [UnitTest]
    public void Sample_LargeCapture_KeepsTheCapAndSpansTheWholeList()
    {
        var features = Enumerable.Range(0, ExportThumbnail.MaxFeatures * 3)
            .Select(id => Square() with { Id = id }).ToArray();

        var sample = ExportThumbnail.Sample(features);

        sample.Should().HaveCount(ExportThumbnail.MaxFeatures);
        sample[0].Id.Should().Be(0);
        sample[^1].Id.Should().BeGreaterThan(features.Length - 4);
    }

    private static Feature Square()
    {
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var polygon = factory.CreatePolygon([
            new Coordinate(-157.9, 21.3), new Coordinate(-157.8, 21.3), new Coordinate(-157.8, 21.4),
            new Coordinate(-157.9, 21.4), new Coordinate(-157.9, 21.3)]);
        return new Feature { Id = 1, Geometry = new WKBWriter().Write(polygon), Attributes = ImmutableDictionary<string, object?>.Empty };
    }
}
