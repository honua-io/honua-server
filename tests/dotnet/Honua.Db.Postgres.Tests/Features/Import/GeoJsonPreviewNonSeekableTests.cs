// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.TestKit.Infrastructure;

namespace Honua.Postgres.Tests.Features.Import;

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

    private static IFileImportService CreateService() =>
        PreviewImportServiceFactory.Create();

}
