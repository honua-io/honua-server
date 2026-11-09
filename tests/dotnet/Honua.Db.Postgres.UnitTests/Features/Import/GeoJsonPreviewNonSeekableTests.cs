// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.TestKit.Infrastructure;

namespace Honua.Db.Postgres.Tests.Features.Import;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class GeoJsonPreviewNonSeekableTests
{
    [Fact]
    public async Task PreviewFileAsync_GeoJsonNonSeekableStream_ReturnsFirstFeature()
    {
        var geoJson = """
            {
              "type": "FeatureCollection",
              "crs": { "type": "name", "properties": { "name": "EPSG:4326" } },
              "features": [
                {
                  "type": "Feature",
                  "geometry": { "type": "Point", "coordinates": [1, 2] },
                  "properties": { "name": "Test Feature" }
                }
              ]
            }
            """;

        var bytes = Encoding.UTF8.GetBytes(geoJson);
        await using var stream = new NonSeekableStream(new MemoryStream(bytes));
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "sample.geojson");

        preview.Format.Should().Be(SupportedFileFormat.GeoJson);
        preview.TotalFeatureCount.Should().Be(1);
        preview.SampleProperties.Should().ContainKey("name");
        preview.SampleProperties["name"].Should().Be("Test Feature");
    }

    [Fact]
    public async Task PreviewFileAsync_GeoJsonNestedProperties_PreservesStructuredJsonValues()
    {
        var geoJson = """
            {
              "type": "FeatureCollection",
              "features": [
                {
                  "type": "Feature",
                  "geometry": { "type": "Point", "coordinates": [1, 2] },
                  "properties": {
                    "name": "Test Feature",
                    "tags": ["harbor", "pacific"],
                    "metadata": { "depth": 12, "public": true }
                  }
                }
              ]
            }
            """;

        var bytes = Encoding.UTF8.GetBytes(geoJson);
        await using var stream = new NonSeekableStream(new MemoryStream(bytes));
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "nested.geojson");

        preview.Format.Should().Be(SupportedFileFormat.GeoJson);
        preview.SampleProperties.Should().ContainKey("tags");
        preview.SampleProperties.Should().ContainKey("metadata");

        var tags = preview.SampleProperties["tags"].Should().BeOfType<JsonElement>().Subject;
        tags.ValueKind.Should().Be(JsonValueKind.Array);
        tags.EnumerateArray().Select(static item => item.GetString()).Should().Equal("harbor", "pacific");

        var metadata = preview.SampleProperties["metadata"].Should().BeOfType<JsonElement>().Subject;
        metadata.ValueKind.Should().Be(JsonValueKind.Object);
        metadata.GetProperty("depth").GetInt32().Should().Be(12);
        metadata.GetProperty("public").GetBoolean().Should().BeTrue();
    }

    [Honua.TestKit.Attributes.UnitTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PreviewFileAsync_GeoJsonLateRootCrs_ReportsDeclaredSource(bool nonSeekable, bool bom)
    {
        var geoJson = "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[1740000,5420000]},\"properties\":{\"padding\":\""
            + new string('x', 20000) + "\"}}],\"crs\":{\"type\":\"name\",\"properties\":{\"name\":\"EPSG:2193\"}}}";
        var bytes = Encoding.UTF8.GetBytes((bom ? "\uFEFF" : "") + geoJson);
        await using Stream stream = nonSeekable ? new NonSeekableStream(new MemoryStream(bytes)) : new MemoryStream(bytes);
        var preview = await CreateService().PreviewFileAsync(stream, "late.geojson");
        preview.TotalFeatureCount.Should().Be(1);
        preview.CoordinateSystem!.DetectedSrid.Should().Be(2193);
        preview.CoordinateSystem.SourceCrsDeclared.Should().BeTrue();
        preview.CoordinateSystem.DetectionMethod.Should().Be("file-metadata");
    }

    [Honua.TestKit.Attributes.UnitTest]
    public async Task PreviewFileAsync_GeoJsonCommentsAndTrailingCommas_UsesFormatDefinedCrs()
    {
        const string json = "{ /* metadata */ \"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[1,2]},\"properties\":{},},],}";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var preview = await CreateService().PreviewFileAsync(stream, "comments.geojson");
        preview.CoordinateSystem!.DetectedSrid.Should().Be(4326);
        preview.CoordinateSystem.SourceCrsDeclared.Should().BeFalse();
        preview.CoordinateSystem.DetectionMethod.Should().Be("format-defined");
    }

    private static IFileImportService CreateService() =>
        PreviewImportServiceFactory.Create();

}
