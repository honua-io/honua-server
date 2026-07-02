// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

// Regression coverage for honua-server#2352 (EsriJSON) and #2353 (WKB): both must be recognised
// on the import path instead of falling through to null (WKB -> generic multipart error) or being
// silently treated as GeoJSON (EsriJSON).
public sealed class EsriJsonWkbFormatDetectionTests
{
    private readonly FileFormatDetectionService _service =
        new(NullLogger<FileFormatDetectionService>.Instance);

    [Theory]
    [InlineData("sample.esrijson")]
    [InlineData("SAMPLE.EsriJson")]
    public void DetectFormat_EsriJsonExtension_ReturnsEsriJson(string fileName)
    {
        _service.DetectFormat(fileName).Should().Be(SupportedFileFormat.EsriJson);
    }

    [Theory]
    [InlineData("sample.wkb")]
    [InlineData("SAMPLE.WKB")]
    public void DetectFormat_WkbExtension_ReturnsWkb(string fileName)
    {
        _service.DetectFormat(fileName).Should().Be(SupportedFileFormat.Wkb);
    }

    [Fact]
    public void GetSupportedExtensions_IncludesEsriJsonAndWkb()
    {
        var extensions = _service.GetSupportedExtensions();
        extensions.Should().Contain(".esrijson");
        extensions.Should().Contain(".wkb");
    }

    [Fact]
    public void DetectFormatFromContent_EsriFeatureSet_ReturnsEsriJson_NotGeoJson()
    {
        const string esriJson = """
            {"geometryType":"esriGeometryPoint","spatialReference":{"wkid":4326},"features":[
            {"attributes":{"zone_code":"030"},"geometry":{"x":-156.30,"y":20.80}}]}
            """;
        var bytes = Encoding.UTF8.GetBytes(esriJson);

        _service.DetectFormatFromContent(bytes, "payload.json")
            .Should().Be(SupportedFileFormat.EsriJson);
    }

    [Fact]
    public void DetectFormatFromContent_EsriPolygonRings_ReturnsEsriJson()
    {
        const string esriJson = """
            {"geometryType":"esriGeometryPolygon","spatialReference":{"wkid":4326},"features":[
            {"attributes":{"id":1},"geometry":{"rings":[[[0,0],[0,1],[1,1],[1,0],[0,0]]]}}]}
            """;
        var bytes = Encoding.UTF8.GetBytes(esriJson);

        _service.DetectFormatFromContent(bytes, "payload.json")
            .Should().Be(SupportedFileFormat.EsriJson);
    }

    [Fact]
    public void DetectFormatFromContent_GeoJsonFeatureCollection_StillReturnsGeoJson()
    {
        const string geoJson = """
            {"type":"FeatureCollection","features":[
            {"type":"Feature","properties":{"zone_code":"030"},"geometry":{"type":"Point","coordinates":[-156.30,20.80]}}]}
            """;
        var bytes = Encoding.UTF8.GetBytes(geoJson);

        _service.DetectFormatFromContent(bytes, "payload.json")
            .Should().Be(SupportedFileFormat.GeoJson);
    }
}
