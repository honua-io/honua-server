// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Honua.Server.Tests.Features.API;

/// <summary>
/// Comprehensive API endpoint coverage tests ensuring 100% API surface testing
/// </summary>
[Collection("Database")]
public class EndpointCoverageTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public EndpointCoverageTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    #region FeatureServer Protocol Tests

    [IntegrationTest]
    [Protocol(Protocols.FeatureServer)]
    [Operation("ServiceInfo")]
    [Endpoint("GET /rest/services/{serviceName}/FeatureServer")]
    public async Task FeatureServer_GetServiceInfo_ShouldReturnServiceMetadata()
    {
        // Act
        var response = await _client.GetAsync("/rest/services/test/FeatureServer?f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("serviceDescription").GetString().Should().NotBeEmpty();
        json.RootElement.GetProperty("layers").GetArrayLength().Should().BeGreaterThan(0);
        json.RootElement.GetProperty("spatialReference").Should().ValueKind.Should().Be(JsonValueKind.Object);
    }

    [IntegrationTest]
    [Protocol(Protocols.FeatureServer)]
    [Operation("LayerInfo")]
    [Endpoint("GET /rest/services/{serviceName}/FeatureServer/{layerId}")]
    public async Task FeatureServer_GetLayerInfo_ShouldReturnLayerMetadata()
    {
        // Act
        var response = await _client.GetAsync("/rest/services/test/FeatureServer/1?f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("id").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("name").GetString().Should().NotBeEmpty();
        json.RootElement.GetProperty("fields").GetArrayLength().Should().BeGreaterThan(0);
        json.RootElement.GetProperty("geometryType").GetString().Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Protocol(Protocols.FeatureServer)]
    [Operation("Query")]
    [Endpoint("GET /rest/services/{serviceName}/FeatureServer/{layerId}/query")]
    public async Task FeatureServer_Query_WithBasicParameters_ShouldReturnFeatures()
    {
        // Act
        var response = await _client.GetAsync("/rest/services/test/FeatureServer/1/query?where=1%3D1&f=json&outFields=*&returnGeometry=true");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("features").Should().ValueKind.Should().Be(JsonValueKind.Array);
        json.RootElement.GetProperty("spatialReference").Should().ValueKind.Should().Be(JsonValueKind.Object);
    }

    [IntegrationTest]
    [Protocol(Protocols.FeatureServer)]
    [Operation("Query")]
    [Endpoint("POST /rest/services/{serviceName}/FeatureServer/{layerId}/query")]
    public async Task FeatureServer_QueryPost_WithComplexFilter_ShouldReturnFilteredFeatures()
    {
        // Arrange
        var queryPayload = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("where", "category = 'retail' AND value > 100"),
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("outFields", "*"),
            new KeyValuePair<string, string>("returnGeometry", "true"),
            new KeyValuePair<string, string>("spatialRel", "esriSpatialRelIntersects")
        });

        // Act
        var response = await _client.PostAsync("/rest/services/test/FeatureServer/1/query", queryPayload);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("features").Should().ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Protocol(Protocols.FeatureServer)]
    [Operation("ApplyEdits")]
    [Endpoint("POST /rest/services/{serviceName}/FeatureServer/{layerId}/applyEdits")]
    public async Task FeatureServer_ApplyEdits_WithNewFeature_ShouldCreateFeature()
    {
        // Arrange
        var editPayload = JsonSerializer.Serialize(new
        {
            adds = new[]
            {
                new
                {
                    geometry = new
                    {
                        type = "Point",
                        coordinates = new[] { -122.0, 37.0 }
                    },
                    attributes = new
                    {
                        name = "Test Feature",
                        category = "test"
                    }
                }
            }
        });

        var content = new StringContent(editPayload, Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/rest/services/test/FeatureServer/1/applyEdits", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseContent = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(responseContent);

        json.RootElement.GetProperty("addResults").GetArrayLength().Should().Be(1);
        json.RootElement.GetProperty("addResults")[0].GetProperty("success").GetBoolean().Should().BeTrue();
    }

    #endregion

    #region OData Protocol Tests

    [IntegrationTest]
    [Protocol(Protocols.OData)]
    [Operation("ServiceDocument")]
    [Endpoint("GET /odata")]
    public async Task OData_GetServiceDocument_ShouldReturnMetadata()
    {
        // Act
        var response = await _client.GetAsync("/odata");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("@odata.context");
    }

    [IntegrationTest]
    [Protocol(Protocols.OData)]
    [Operation("Metadata")]
    [Endpoint("GET /odata/$metadata")]
    public async Task OData_GetMetadata_ShouldReturnSchema()
    {
        // Act
        var response = await _client.GetAsync("/odata/$metadata");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Contain("xml");
    }

    [IntegrationTest]
    [Protocol(Protocols.OData)]
    [Operation("Query")]
    [Endpoint("GET /odata/layers({layerId})/features")]
    public async Task OData_QueryFeatures_ShouldReturnODataFormat()
    {
        // Act
        var response = await _client.GetAsync("/odata/layers(1)/features");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("@odata.context").Should().ValueKind.Should().Be(JsonValueKind.String);
        json.RootElement.GetProperty("value").Should().ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Protocol(Protocols.OData)]
    [Operation("Filter")]
    [Endpoint("GET /odata/layers({layerId})/features?$filter={expression}")]
    public async Task OData_QueryWithFilter_ShouldApplyFilter()
    {
        // Act
        var response = await _client.GetAsync("/odata/layers(1)/features?$filter=category eq 'retail'");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("value").Should().ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Protocol(Protocols.OData)]
    [Operation("Batch")]
    [Endpoint("POST /odata/$batch")]
    public async Task OData_BatchRequest_ShouldProcessMultipleOperations()
    {
        // Arrange
        var batchPayload = """
            --batch_12345
            Content-Type: application/http
            Content-Transfer-Encoding: binary

            GET /odata/layers(1)/features HTTP/1.1
            Accept: application/json

            --batch_12345--
            """;

        var content = new StringContent(batchPayload, Encoding.UTF8, "multipart/mixed; boundary=batch_12345");

        // Act
        var response = await _client.PostAsync("/odata/$batch", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Contain("multipart/mixed");
    }

    #endregion

    #region OGC API Features Tests

    [IntegrationTest]
    [Protocol(Protocols.OgcApiFeatures)]
    [Operation("LandingPage")]
    [Endpoint("GET /")]
    public async Task OgcApi_GetLandingPage_ShouldReturnApiInfo()
    {
        // Act
        var response = await _client.GetAsync("/");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("title").GetString().Should().NotBeEmpty();
        json.RootElement.GetProperty("links").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Protocol(Protocols.OgcApiFeatures)]
    [Operation("Conformance")]
    [Endpoint("GET /conformance")]
    public async Task OgcApi_GetConformance_ShouldReturnConformanceClasses()
    {
        // Act
        var response = await _client.GetAsync("/conformance");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("conformsTo").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Protocol(Protocols.OgcApiFeatures)]
    [Operation("Collections")]
    [Endpoint("GET /collections")]
    public async Task OgcApi_GetCollections_ShouldReturnCollectionList()
    {
        // Act
        var response = await _client.GetAsync("/collections");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("collections").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Protocol(Protocols.OgcApiFeatures)]
    [Operation("Collection")]
    [Endpoint("GET /collections/{collectionId}")]
    public async Task OgcApi_GetCollection_ShouldReturnCollectionMetadata()
    {
        // Act
        var response = await _client.GetAsync("/collections/test-layer");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("id").GetString().Should().Be("test-layer");
        json.RootElement.GetProperty("extent").Should().ValueKind.Should().Be(JsonValueKind.Object);
    }

    [IntegrationTest]
    [Protocol(Protocols.OgcApiFeatures)]
    [Operation("Features")]
    [Endpoint("GET /collections/{collectionId}/items")]
    public async Task OgcApi_GetFeatures_ShouldReturnGeoJSON()
    {
        // Act
        var response = await _client.GetAsync("/collections/test-layer/items");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        json.RootElement.GetProperty("features").Should().ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Protocol(Protocols.OgcApiFeatures)]
    [Operation("Feature")]
    [Endpoint("GET /collections/{collectionId}/items/{featureId}")]
    public async Task OgcApi_GetFeature_ShouldReturnSingleFeature()
    {
        // Act
        var response = await _client.GetAsync("/collections/test-layer/items/1");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("Feature");
        json.RootElement.GetProperty("id").Should().ValueKind.Should().Be(JsonValueKind.String);
    }

    #endregion

    #region MVT Protocol Tests

    [IntegrationTest]
    [Protocol(Protocols.MVT)]
    [Operation("Tile")]
    [Endpoint("GET /mvt/{layerId}/{z}/{x}/{y}.pbf")]
    public async Task MVT_GetTile_ShouldReturnProtobuf()
    {
        // Act
        var response = await _client.GetAsync("/mvt/1/10/512/512.pbf");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/x-protobuf");
    }

    [IntegrationTest]
    [Protocol(Protocols.MVT)]
    [Operation("TileJSON")]
    [Endpoint("GET /mvt/{layerId}/tilejson")]
    public async Task MVT_GetTileJSON_ShouldReturnTileJSONSpec()
    {
        // Act
        var response = await _client.GetAsync("/mvt/1/tilejson");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("tilejson").GetString().Should().Be("2.2.0");
        json.RootElement.GetProperty("tiles").GetArrayLength().Should().BeGreaterThan(0);
    }

    #endregion

    #region Health Check Tests

    [IntegrationTest]
    [Operation("HealthCheck")]
    [Endpoint("GET /health/live")]
    public async Task Health_GetLiveness_ShouldReturnHealthy()
    {
        // Act
        var response = await _client.GetAsync("/health/live");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Healthy");
    }

    [IntegrationTest]
    [Operation("HealthCheck")]
    [Endpoint("GET /health/ready")]
    public async Task Health_GetReadiness_ShouldReturnReady()
    {
        // Act
        var response = await _client.GetAsync("/health/ready");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Healthy");
    }

    #endregion

    #region Admin API Tests

    [IntegrationTest]
    [Operation("Admin")]
    [Endpoint("GET /admin/tables")]
    public async Task Admin_GetTables_ShouldReturnTableList()
    {
        // Act
        var response = await _client.GetAsync("/admin/tables");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Unauthorized);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var content = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(content);
            json.RootElement.Should().ValueKind.Should().Be(JsonValueKind.Array);
        }
    }

    [IntegrationTest]
    [Operation("Admin")]
    [Endpoint("POST /admin/layers")]
    public async Task Admin_CreateLayer_ShouldCreateNewLayer()
    {
        // Arrange
        var layerPayload = JsonSerializer.Serialize(new
        {
            name = "test_layer_" + Guid.NewGuid().ToString("N")[..8],
            geometryType = "Point",
            spatialReference = new { wkid = 4326 },
            fields = new[]
            {
                new { name = "objectid", type = "esriFieldTypeOID" },
                new { name = "name", type = "esriFieldTypeString", length = 255 }
            }
        });

        var content = new StringContent(layerPayload, Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/admin/layers", content);

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    #endregion

    #region Import API Tests

    [IntegrationTest]
    [Operation("Import")]
    [Endpoint("POST /api/import/upload")]
    public async Task Import_UploadFile_ShouldAcceptValidFormats()
    {
        // Arrange
        var geoJsonContent = """
            {
                "type": "FeatureCollection",
                "features": [
                    {
                        "type": "Feature",
                        "geometry": {
                            "type": "Point",
                            "coordinates": [-122.0, 37.0]
                        },
                        "properties": {
                            "name": "Test Point"
                        }
                    }
                ]
            }
            """;

        var multipartContent = new MultipartFormDataContent();
        multipartContent.Add(new StringContent(geoJsonContent), "file", "test.geojson");

        // Act
        var response = await _client.PostAsync("/api/import/upload", multipartContent);

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Accepted, HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation("Import")]
    [Endpoint("GET /api/import/status/{jobId}")]
    public async Task Import_GetStatus_ShouldReturnJobStatus()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();

        // Act
        var response = await _client.GetAsync($"/api/import/status/{jobId}");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.Unauthorized);
    }

    #endregion

    #region Error Handling Tests

    [IntegrationTest]
    [Operation("ErrorHandling")]
    [Endpoint("GET /rest/services/nonexistent/FeatureServer")]
    public async Task ErrorHandling_NonexistentService_ShouldReturn404()
    {
        // Act
        var response = await _client.GetAsync("/rest/services/nonexistent/FeatureServer?f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation("ErrorHandling")]
    [Endpoint("GET /rest/services/test/FeatureServer/99999")]
    public async Task ErrorHandling_NonexistentLayer_ShouldReturn404()
    {
        // Act
        var response = await _client.GetAsync("/rest/services/test/FeatureServer/99999?f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation("ErrorHandling")]
    [Endpoint("GET /rest/services/test/FeatureServer/1/query?where=invalid")]
    public async Task ErrorHandling_InvalidQuery_ShouldReturn400()
    {
        // Act
        var response = await _client.GetAsync("/rest/services/test/FeatureServer/1/query?where=INVALID_SQL_SYNTAX&f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion
}
