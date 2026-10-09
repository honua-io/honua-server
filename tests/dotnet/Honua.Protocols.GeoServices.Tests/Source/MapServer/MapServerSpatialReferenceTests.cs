// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

[Collection("Database.GeoServicesMapServer")]
[Protocol(TestProtocols.MapServer)]
public sealed class MapServerSpatialReferenceTests : MapServerEndpointTestBase
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}/query")]
    public async Task MapQuery_DefaultGeometryUsesMapReference_ExplicitReferenceAndFeatureServerRemainGeographic()
    {
        foreach (var (protocol, extra, srid) in new[]
        {
            ("MapServer", "", 3857), ("MapServer", "&outSR=4326", 4326), ("FeatureServer", "", 4326)
        })
        {
            var response = await Fixture.Client.GetAsync(QueryUrl(protocol, "json", extra));
            var content = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, content);
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            root.TryGetProperty("error", out _).Should().BeFalse(content);
            root.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(srid);
            var geometry = root.GetProperty("features")[0].GetProperty("geometry");
            var longitude = -122.5;
            var latitude = 37.5;
            var x = srid == 4326 ? longitude : 6378137 * longitude * Math.PI / 180;
            var y = srid == 4326 ? latitude : 6378137 * Math.Log(Math.Tan(Math.PI / 4 + latitude * Math.PI / 360));
            geometry.GetProperty("x").GetDouble().Should().BeApproximately(x, 0.01);
            geometry.GetProperty("y").GetDouble().Should().BeApproximately(y, 0.01);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}/query")]
    public async Task MapQuery_GeoJsonDefaultRemainsWgs84()
    {
        var response = await Fixture.Client.GetAsync(QueryUrl("MapServer", "geojson"));
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        using var document = JsonDocument.Parse(content);
        var coordinates = document.RootElement.GetProperty("features")[0].GetProperty("geometry").GetProperty("coordinates");
        coordinates[0].GetDouble().Should().BeApproximately(-122.5, 0.000001);
        coordinates[1].GetDouble().Should().BeApproximately(37.5, 0.000001);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}/query")]
    public async Task MapQuery_CloudNativeDefaultsRemainValidWgs84()
    {
        foreach (var format in new[] { "arrow", "parquet" })
        {
            var response = await Fixture.Client.GetAsync(QueryUrl("MapServer", format));
            var payload = await response.Content.ReadAsByteArrayAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var stream = new MemoryStream(payload);
            if (format == "arrow")
            {
                using var reader = new ArrowStreamReader(stream);
                using var batch = await reader.ReadNextRecordBatchAsync();
                AssertGeographicPoint(batch!);
            }
            else
            {
                using var reader = new ParquetSharp.Arrow.FileReader(stream);
                using var batches = reader.GetRecordBatchReader();
                using var batch = await batches.ReadNextRecordBatchAsync();
                AssertGeographicPoint(batch!);
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}/query")]
    public async Task MapQuery_PbfDefaultSpatialReferenceIsMapReference()
    {
        var response = await Fixture.Client.GetAsync(QueryUrl("MapServer", "pbf"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsByteArrayAsync();
        var query = FeatureServer.Services.PbfQueryFormatterTests.GetFirstLengthDelimitedField(payload, 2);
        var features = FeatureServer.Services.PbfQueryFormatterTests.GetFirstLengthDelimitedField(query, 1);
        var reference = FeatureServer.Services.PbfQueryFormatterTests.GetFirstLengthDelimitedField(features, 8);
        FeatureServer.Services.PbfQueryFormatterTests.GetFirstVarintField(reference, 1).Should().Be(3857);
        FeatureServer.Services.PbfQueryFormatterTests.GetFirstLengthDelimitedField(features, 15).Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}/query")]
    public async Task MapQuery_OmittedInputReferenceUsesMapCoordinates()
    {
        var envelope = Uri.EscapeDataString("{\"xmin\":-13640000,\"ymin\":4500000,\"xmax\":-13630000,\"ymax\":4515000}");
        var response = await Fixture.Client.GetAsync(QueryUrl("MapServer", "json",
            $"&geometry={envelope}&geometryType=esriGeometryEnvelope&spatialRel=esriSpatialRelIntersects"));
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("features").GetArrayLength().Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}/queryRelatedRecords")]
    public async Task MapRelatedQuery_DefaultReferenceIsMap_ExplicitAndFeatureServerRemainGeographic()
    {
        foreach (var (protocol, extra, srid) in new[]
        {
            ("MapServer", "", 3857), ("MapServer", "&outSR=4326", 4326), ("FeatureServer", "", 4326)
        })
        {
            var response = await Fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/{protocol}/{WebAppFixture.TestLayerId}/queryRelatedRecords?objectIds=1&relationshipId=1&returnGeometry=true{extra}");
            var content = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, content);
            using var document = JsonDocument.Parse(content);
            document.RootElement.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(srid);
            document.RootElement.GetProperty("relatedRecordGroups").GetArrayLength().Should().Be(1);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/layers")]
    public async Task MapAllLayers_AdvertisesMapReferenceAndProjectedExtentSeparatelyFromSource()
    {
        var response = await Fixture.Client.GetAsync($"/rest/services/{WebAppFixture.TestServiceId}/MapServer/layers?f=json");
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        using var document = JsonDocument.Parse(content);
        var layer = document.RootElement.GetProperty("layers").EnumerateArray()
            .Single(value => value.GetProperty("id").GetInt32() == WebAppFixture.TestLayerId);
        layer.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(3857);
        layer.GetProperty("sourceSpatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
        layer.GetProperty("extent").GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(3857);
        layer.GetProperty("extent").GetProperty("xmin").GetDouble().Should().BeApproximately(-13692297.3676, 0.1);
    }

    private static void AssertGeographicPoint(RecordBatch batch)
    {
        batch.Length.Should().Be(1);
        var point = new WKBReader().Read(((BinaryArray)batch.Column("geometry")).GetBytes(0).ToArray());
        point.Coordinate.X.Should().BeApproximately(-122.5, 0.000001);
        point.Coordinate.Y.Should().BeApproximately(37.5, 0.000001);
    }

    private static string QueryUrl(string protocol, string format, string extra = "")
        => $"/rest/services/{WebAppFixture.TestServiceId}/{protocol}/{WebAppFixture.TestLayerId}/query?objectIds=1&outFields=*&returnGeometry=true&f={format}{extra}";
}
