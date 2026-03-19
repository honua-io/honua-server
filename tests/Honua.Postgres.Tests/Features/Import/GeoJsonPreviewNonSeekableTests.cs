// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
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

    private static IFileImportService CreateService() =>
        PreviewImportServiceFactory.Create();

}
