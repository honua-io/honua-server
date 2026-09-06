// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

public sealed partial class RasterExecutionProofTests
{
    [Theory]
    [InlineData("PNG")]
    [InlineData("GTiff")]
    [InlineData("COG")]
    public async Task RasterFormat_MultibandFixture_PreservesSamplesEncodingAndSupportedMetadata(string format)
    {
        var output = await ExecuteRaster("conversion.raster-format", ("source", Input("conversion-rgb.tif")),
            ("targetFormat", format));
        output.GetProperty("driver").GetString().Should().Be(format == "PNG" ? "PNG" : "GTiff");
        output.GetProperty("width").GetInt32().Should().Be(3);
        output.GetProperty("height").GetInt32().Should().Be(2);
        output.GetProperty("bands").GetArrayLength().Should().Be(3);
        for (var i = 0; i < 3; i++)
        {
            AssertBand(output, i, [10 + i, 20 + i, 30 + i, 40 + i, 50 + i, 255], "Byte", 255);
            output.GetProperty("bands")[i].GetProperty("color").GetString().Should().Be(new[] { "Red", "Green", "Blue" }[i]);
        }
        if (format != "PNG")
        {
            AssertGrid(output, 3, 2, 4326, [10, 0.5, 0, 20, 0, -0.5], 3);
        }
        // PNG carries color and transparent nodata, but has no embedded GIS CRS.
        if (format == "COG")
        {
            output.GetProperty("imageStructure").GetProperty("LAYOUT").GetString().Should().Be("COG");
            var validation = await Run("python3", ["-m", "osgeo_utils.samples.validate_cloud_optimized_geotiff", "decoded.tif"]);
            validation.Should().NotContain("ERROR");
        }
    }

    [Theory]
    [InlineData("4")]
    [InlineData("8")]
    public async Task Polygonize_ClassifiedFixture_PreservesRegionsTopologyAndExcludesNoData(string connectivity)
    {
        using var json = JsonDocument.Parse(await Execute("conversion.polygonize",
            ("source", Input("conversion-classes.tif")), ("connectedness", connectivity), ("fieldName", "class_value")));
        var root = json.RootElement;
        root.GetProperty("crs").GetProperty("properties").GetProperty("name").GetString().Should().Contain("CRS84");
        var features = root.GetProperty("features").EnumerateArray().ToArray();
        features.Should().HaveCount(connectivity == "4" ? 3 : 2);
        features.Sum(f => new GeoJsonReader().Read<Geometry>(f.GetProperty("geometry").GetRawText()).Area).Should().Be(5);
        foreach (var feature in features)
        {
            var value = feature.GetProperty("properties").GetProperty("class_value").GetInt32();
            value.Should().BeOneOf(1, 2);
            var geometry = new GeoJsonReader().Read<Geometry>(feature.GetProperty("geometry").GetRawText());
            geometry.IsValid.Should().BeTrue();
            geometry.Area.Should().Be(value == 1 ? 3 : connectivity == "4" ? 1 : 2);
            var expected = value == 1
                ? new WKTReader().Read("POLYGON ((10 20,12 20,12 19,11 19,11 18,10 18,10 20))")
                : connectivity == "8"
                    ? new WKTReader().Read("MULTIPOLYGON (((12 19,13 19,13 18,12 18,12 19)),((11 18,12 18,12 17,11 17,11 18)))")
                    : geometry.EnvelopeInternal.MinX == 12
                        ? new WKTReader().Read("POLYGON ((12 19,13 19,13 18,12 18,12 19))")
                        : new WKTReader().Read("POLYGON ((11 18,12 18,12 17,11 17,11 18))");
            geometry.EqualsTopologically(expected).Should().BeTrue("cell boundaries are derived from the input matrix");
            geometry.EnvelopeInternal.Should().Be(expected.EnvelopeInternal);
        }
    }
}
