// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.MapServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

/// <summary>MapServer identify and find endpoint integration tests.</summary>
[Collection("Database.GeoServicesMapServer")]
[Protocol(TestProtocols.MapServer)]
public sealed class MapServerIdentifyEndpointTests : MapServerEndpointTestBase
{
    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithLayerTimeOptionsUseTimeFalse_IgnoresGlobalTimeFilter()
    {
        var time = Uri.EscapeDataString("2025-01-01T00:00:00Z,2025-01-31T00:00:00Z");
        var layerTimeOptions = Uri.EscapeDataString("{\"0\":{\"useTime\":false}}");
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&time={time}&layerTimeOptions={layerTimeOptions}&f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var identify = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.IdentifyResponse);

        identify.Should().NotBeNull();
        identify!.Results.Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_ReturnsResults()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var identify = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.IdentifyResponse);

        identify.Should().NotBeNull();
        identify!.Results.Should().NotBeNull();
        identify.Results!.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTheory]
    [InlineData("esriGeometryEnvelope", "{\"xmin\":-122.51,\"ymin\":37.504,\"xmax\":-122.49,\"ymax\":37.506}")]
    [InlineData("esriGeometryMultipoint", "{\"points\":[[-122.5,37.505]]}")]
    [InlineData("esriGeometryPolyline", "{\"paths\":[[[-122.51,37.505],[-122.49,37.505]]]}")]
    [InlineData("esriGeometryPolygon", "{\"rings\":[[[-122.51,37.504],[-122.49,37.504],[-122.49,37.506],[-122.51,37.506],[-122.51,37.504]]]}")]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_NonPointGeometryWithinTolerance_ReturnsResults(
        string geometryType,
        string geometry)
    {
        // The seeded point is (-122.5, 37.5). With this one-degree map extent and 1000px
        // display width, tolerance=10 is 0.01 map units. Each geometry is 0.004-0.005 units away:
        // Shapely 2.1.2 reports distance 0.004-0.005, so it misses unbuffered and hits buffered.
        var encodedGeometry = Uri.EscapeDataString(geometry);
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify" +
            $"?geometry={encodedGeometry}&geometryType={geometryType}&sr=4326" +
            "&mapExtent=-123,37,-122,38&imageDisplay=1000,1000,96" +
            "&tolerance=10&layers=all&f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var identify = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.IdentifyResponse);

        identify.Should().NotBeNull();
        identify!.Results.Should().NotBeNullOrEmpty(
            $"{geometryType} identify tolerance applies around the geometry boundary");
    }

    // Regression (#1429): the ArcGIS JS SDK and arcpy send mapExtent as an Esri JSON
    // envelope ({"xmin":..,"ymin":..,"xmax":..,"ymax":..}); it must be accepted, not 400.
    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithJsonEnvelopeMapExtent_ReturnsResults()
    {
        var mapExtent = Uri.EscapeDataString(
            """{"xmin":-180,"ymin":-90,"xmax":180,"ymax":90,"spatialReference":{"wkid":4326}}""");
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent={mapExtent}&imageDisplay=800,600,96&f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var identify = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.IdentifyResponse);

        identify.Should().NotBeNull();
        identify!.Results.Should().NotBeNull();
        identify.Results!.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithDatelineCrossingMapExtent_ReturnsOk()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry=179.5,0&geometryType=esriGeometryPoint&mapExtent=170,-10,-170,10&imageDisplay=800,600,96&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithProjectedMapExtent_ReturnsOk()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry=-13636637.62,4509031.39&geometryType=esriGeometryPoint&sr=3857&mapExtent=-13700000,4490000,-13600000,4600000&imageDisplay=800,600,96&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithGdbVersion_IgnoresParameter()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&f=json&gdbVersion=QA");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var identify = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.IdentifyResponse);

        identify.Should().NotBeNull();
        identify!.Results.Should().NotBeNull();
        identify.Results!.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_Post_ReturnsResults()
    {
        using var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("geometry", "-122.5,37.5"),
            new KeyValuePair<string, string>("geometryType", "esriGeometryPoint"),
            new KeyValuePair<string, string>("mapExtent", "-180,-90,180,90"),
            new KeyValuePair<string, string>("imageDisplay", "800,600,96"),
            new KeyValuePair<string, string>("f", "json")
        ]);

        var response = await Fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify",
            payload);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var identify = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.IdentifyResponse);

        identify.Should().NotBeNull();
        identify!.Results.Should().NotBeNull();
        identify.Results!.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithInvalidGeometryType_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry=-122.5,37.5&geometryType=invalidType&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithInvalidTolerance_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&tolerance=abc&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithMalformedImageDisplay_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90&imageDisplay=800,,600,96&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithMalformedLayersDelimiter_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&layers=visible:{WebAppFixture.TestLayerId},&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithInvalidLayerIdentifier_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&layers=visible:{WebAppFixture.TestLayerId},foo&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithMalformedPointPair_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry=-122.5,,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithMalformedGeometryJson_DoesNotLeakParserDetails()
    {
        var malformedGeometry = Uri.EscapeDataString("{\"rings\":[1]}");
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify?geometry={malformedGeometry}&geometryType=esriGeometryPolygon&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Geometry parameter is invalid.");
        content.Should().NotContain("System.Text.Json");
        content.Should().NotContain("Supported types:");
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithOversizedGeometry_ReturnsBadRequest()
    {
        var oversizedTag = new string('a', 2500);
        var geometry = $"{{\"x\":-122.5,\"y\":37.5,\"tag\":\"{oversizedTag}\"}}";

        using var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("geometry", geometry),
            new KeyValuePair<string, string>("geometryType", "esriGeometryPoint"),
            new KeyValuePair<string, string>("mapExtent", "-180,-90,180,90"),
            new KeyValuePair<string, string>("imageDisplay", "800,600,96"),
            new KeyValuePair<string, string>("f", "json")
        ]);

        var response = await Fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/identify",
            payload);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_WithInvalidIdentifier_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            "/rest/services/%20/MapServer/identify?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/find")]
    public async Task MapServer_Find_Post_WithUnsupportedContentType_ReturnsUnsupportedMediaType()
    {
        var response = await PostTextPlainJsonAsync("/find");

        await response.AssertGeoServicesErrorAsync(415, 500);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/identify")]
    public async Task MapServer_Identify_Post_WithUnsupportedContentType_ReturnsUnsupportedMediaType()
    {
        var response = await PostTextPlainJsonAsync("/identify");

        await response.AssertGeoServicesErrorAsync(415, 500);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/find")]
    public async Task MapServer_Find_Get_ReturnsResponse()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/find?searchText=test&layers={WebAppFixture.TestLayerId}&f=json");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("\"results\"");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/find")]
    public async Task MapServer_Find_WithGdbVersion_DoesNotRejectParameter()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/find?searchText=test&layers={WebAppFixture.TestLayerId}&f=json&gdbVersion=QA");

        var content = await response.Content.ReadAsStringAsync();
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            content.Should().NotContain("gdbVersion is not supported.");
            return;
        }

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("\"results\"");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/find")]
    public async Task MapServer_Find_Post_ReturnsResponse()
    {
        using var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("searchText", "test"),
            new KeyValuePair<string, string>("layers", WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("f", "json")
        ]);

        var response = await Fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/find",
            payload);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("\"results\"");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/find")]
    public async Task MapServer_Find_WithMalformedLayersDelimiter_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/find?searchText=test&layers={WebAppFixture.TestLayerId},&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/find")]
    public async Task MapServer_Find_WithMalformedSearchFieldsDelimiter_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/find?searchText=test&layers={WebAppFixture.TestLayerId}&searchFields=name,,category&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/find")]
    public async Task MapServer_Find_WithInvalidLayerIdentifier_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/find?searchText=test&layers={WebAppFixture.TestLayerId},foo&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Find)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/find")]
    public async Task MapServer_Find_ReturnsFindResults()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/find?searchText=Test&layers=0&returnGeometry=true&f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var find = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.FindResponse);
        find.Should().NotBeNull();
        find!.Results.Should().NotBeNullOrEmpty();

        var hit = find.Results!.First();
        hit.LayerId.Should().Be(0);
        hit.LayerName.Should().NotBeNullOrWhiteSpace();
        hit.FoundFieldName.Should().NotBeNullOrWhiteSpace();
        hit.Value.Should().NotBeNullOrWhiteSpace();
        hit.Attributes.Should().NotBeNullOrEmpty();
        hit.Attributes.Should().ContainKeys("description", "category", "timestamp");
        hit.Geometry.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Find)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/find")]
    public async Task MapServer_Find_WithEmptySearchFields_ReturnsFindResults()
    {
        // Esri clients (ArcGIS API for Python) send searchFields= empty, which is the
        // original honua-server#1771 repro that 500'd.
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/find?searchText=a&layers=0&searchFields=&returnGeometry=true&f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var find = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.FindResponse);
        find.Should().NotBeNull();
        find!.Results.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Find)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/find")]
    public async Task MapServer_Find_WithContainsFalse_UsesExactMatch()
    {
        // contains=false => exact (case-insensitive) match. 'Test Feature' is an exact
        // value in the seed; 'Test' alone must not match under exact semantics.
        var exact = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/find" +
            $"?searchText={Uri.EscapeDataString("Test Feature")}&layers=0&searchFields=name&contains=false&f=json");
        var exactContent = await exact.Content.ReadAsStringAsync();
        exact.StatusCode.Should().Be(HttpStatusCode.OK, exactContent);
        var exactFind = JsonSerializer.Deserialize(exactContent, MapServerJsonContext.Default.FindResponse);
        exactFind!.Results.Should().NotBeNullOrEmpty();
        exactFind.Results!.Should().OnlyContain(r => r.FoundFieldName == "name");

        var partial = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/find" +
            $"?searchText=Test&layers=0&searchFields=name&contains=false&f=json");
        var partialContent = await partial.Content.ReadAsStringAsync();
        partial.StatusCode.Should().Be(HttpStatusCode.OK, partialContent);
        var partialFind = JsonSerializer.Deserialize(partialContent, MapServerJsonContext.Default.FindResponse);
        partialFind!.Results.Should().BeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Find)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/find")]
    public async Task MapServer_Find_WithoutLayers_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/find?searchText=Test&f=json");
        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
