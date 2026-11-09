// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.FileImport.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed class CoordinateSystemPreviewTests
{
    [UnitTheory]
    [InlineData("point.kml", "<kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document><Placemark><Point><coordinates>174,-41</coordinates></Point></Placemark></Document></kml>", 4326, false, "format-defined")]
    [InlineData("point.gpx", "<gpx version=\"1.1\" xmlns=\"http://www.topografix.com/GPX/1/1\"><wpt lat=\"-41\" lon=\"174\" /></gpx>", 4326, false, "format-defined")]
    [InlineData("point.esrijson", "{\"spatialReference\":{\"wkid\":2193},\"geometryType\":\"esriGeometryPoint\",\"features\":[{\"geometry\":{\"x\":1740000,\"y\":5420000},\"attributes\":{}}]}", 2193, true, "file-metadata")]
    [InlineData("point.esrijson", "{\"geometryType\":\"esriGeometryPoint\",\"features\":[{\"geometry\":{\"x\":1740000,\"y\":5420000},\"attributes\":{}}]}", null, false, "unknown")]
    [InlineData("point.esrijson", "{\"spatialReference\":{\"wkt\":\"unresolvable\"},\"features\":[{\"geometry\":{\"x\":1,\"y\":2},\"attributes\":{}}]}", null, true, "file-metadata")]
    [InlineData("point.wkt", "SRID=2193;POINT(1740000 5420000)", 2193, true, "file-metadata")]
    [InlineData("point.wkt", "POINT(1740000 5420000)", null, false, "unknown")]
    public async Task PreviewFileAsync_TextFormat_ReportsSourceProvenance(string name, string text, int? srid, bool declared, string method)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var preview = await PreviewImportServiceFactory.Create().PreviewFileAsync(stream, name);
        preview.TotalFeatureCount.Should().Be(1);
        preview.CoordinateSystem!.DetectedSrid.Should().Be(srid);
        preview.CoordinateSystem.SourceCrsDeclared.Should().Be(declared);
        preview.CoordinateSystem.DetectionMethod.Should().Be(method);
        preview.CoordinateSystem.RequiresSourceOverride.Should().Be(!srid.HasValue);
    }

    [UnitTheory]
    [InlineData("EPSG:2193", 2193)]
    [InlineData("unknown:projection", null)]
    public async Task PreviewFileAsync_Gml_ReportsDeclaredSource(string srsName, int? srid)
    {
        var xml = $"""
            <wfs:FeatureCollection xmlns:wfs="http://www.opengis.net/wfs" xmlns:gml="http://www.opengis.net/gml" xmlns:sf="http://example.test" srsName="{srsName}">
              <gml:featureMember><sf:Place><sf:geometry><gml:Point><gml:pos>1740000 5420000</gml:pos></gml:Point></sf:geometry></sf:Place></gml:featureMember>
            </wfs:FeatureCollection>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var preview = await PreviewImportServiceFactory.Create().PreviewFileAsync(stream, "point.gml");
        preview.CoordinateSystem!.DetectedSrid.Should().Be(srid);
        preview.CoordinateSystem.SourceCrsDeclared.Should().BeTrue();
        preview.CoordinateSystem.RequiresSourceOverride.Should().Be(!srid.HasValue);
    }

    [UnitTest]
    public async Task InspectCoordinateSystemAsync_UnboundedToken_RequiresExplicitSource()
    {
        var json = "{\"type\":\"FeatureCollection\",\"padding\":\"" + new string('x', 17 * 1024 * 1024) + "\",\"features\":[]}";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var coordinateSystem = await StreamingGeoJsonReader.InspectCoordinateSystemAsync(stream, CancellationToken.None);
        coordinateSystem.DetectedSrid.Should().BeNull();
        coordinateSystem.RequiresSourceOverride.Should().BeTrue();
        stream.Position.Should().Be(0);
    }
}
