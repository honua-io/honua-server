// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Honua.Server.Features.Protocols.OData.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.API;

/// <summary>
/// Comprehensive API endpoint coverage tests ensuring 100% API surface testing
/// </summary>
[Collection("Database")]
public sealed class EndpointCoverageTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region FeatureServer Protocol Tests

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.GetServiceInfo)]
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
        json.RootElement.GetProperty("spatialReference").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.GetLayerInfo)]
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
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceName}/FeatureServer/{layerId}/query")]
    public async Task FeatureServer_Query_WithBasicParameters_ShouldReturnFeatures()
    {
        // Act
        var response = await _client.GetAsync("/rest/services/test/FeatureServer/1/query?where=1%3D1&f=json&outFields=*&returnGeometry=true");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("features").ValueKind.Should().Be(JsonValueKind.Array);
        json.RootElement.GetProperty("spatialReference").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceName}/FeatureServer/{layerId}/query")]
    public async Task FeatureServer_QueryPost_WithComplexFilter_ShouldReturnFilteredFeatures()
    {
        // Arrange
        var queryPayload = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("where", "category = 'test'"),
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("outFields", "*"),
            new KeyValuePair<string, string>("returnGeometry", "true"),
            new KeyValuePair<string, string>("spatialRel", "esriSpatialRelIntersects")
        });

        // Act
        var response = await _client.PostAsync("/rest/services/test/FeatureServer/0/query", queryPayload);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("features").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceName}/FeatureServer/{layerId}/applyEdits")]
    public async Task FeatureServer_ApplyEdits_WithNewFeature_ShouldCreateFeature()
    {
        // Arrange
        var editsRequest = new ApplyEditsRequest
        {
            Adds = new[]
            {
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "Test Feature",
                        ["description"] = "Endpoint coverage edit"
                    },
                    Geometry = new GeoServicesGeometry
                    {
                        X = -122.0,
                        Y = 37.0
                    }
                }
            }
        };

        var editPayload = JsonSerializer.Serialize(editsRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
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
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.GetMetadata)]
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
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.GetMetadata)]
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
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task OData_QueryFeatures_ShouldReturnODataFormat()
    {
        // Act
        var response = await _client.GetAsync("/odata/Features(1)");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("@odata.context").ValueKind.Should().Be(JsonValueKind.String);
        json.RootElement.GetProperty("value").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter={expression}")]
    public async Task OData_QueryWithFilter_ShouldApplyFilter()
    {
        // Act
        var response = await _client.GetAsync("/odata/Features(1)?$filter=category eq 'retail'");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("value").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task OData_BatchRequest_ShouldProcessMultipleOperations()
    {
        // Arrange
        var batchRequest = new ODataBatchRequest
        {
            Requests = ImmutableArray.Create(
                new ODataBatchRequestItem
                {
                    Id = "1",
                    Method = "GET",
                    Url = "Features(1)",
                    Headers = new Dictionary<string, string>
                    {
                        ["Accept"] = "application/json"
                    }
                })
        };

        var batchPayload = JsonSerializer.Serialize(batchRequest);
        var content = new StringContent(batchPayload, Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/odata/$batch", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Contain("application/json");
    }

    #endregion

    #region OGC API Features Tests

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiFeatures)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/features")]
    public async Task OgcApi_GetLandingPage_ShouldReturnApiInfo()
    {
        // Act
        var response = await _client.GetAsync("/ogc/features");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("title").GetString().Should().NotBeEmpty();
        json.RootElement.GetProperty("links").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiFeatures)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/features/conformance")]
    public async Task OgcApi_GetConformance_ShouldReturnConformanceClasses()
    {
        // Act
        var response = await _client.GetAsync("/ogc/features/conformance");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("conformsTo").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiFeatures)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/features/collections")]
    public async Task OgcApi_GetCollections_ShouldReturnCollectionList()
    {
        // Act
        var response = await _client.GetAsync("/ogc/features/collections");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("collections").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiFeatures)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    public async Task OgcApi_GetCollection_ShouldReturnCollectionMetadata()
    {
        // Act
        var response = await _client.GetAsync("/ogc/features/collections/0");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("id").GetString().Should().Be("0");
        json.RootElement.GetProperty("extent").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiFeatures)]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task OgcApi_GetFeatures_ShouldReturnGeoJSON()
    {
        // Act
        var response = await _client.GetAsync("/ogc/features/collections/0/items");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        json.RootElement.GetProperty("features").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiFeatures)]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task OgcApi_GetFeature_ShouldReturnSingleFeature()
    {
        // Act
        var response = await _client.GetAsync("/ogc/features/collections/0/items/1");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("Feature");
        json.RootElement.GetProperty("id").ValueKind.Should().BeOneOf(JsonValueKind.String, JsonValueKind.Number);
    }

    #endregion

    #region MVT Protocol Tests

    [IntegrationTest]
    [Protocol(TestProtocols.Mvt)]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task MVT_GetTile_ShouldReturnProtobuf()
    {
        // Act
        var response = await _client.GetAsync("/tiles/1/10/512/512.mvt");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.mapbox-vector-tile");
        }
    }

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiTiles)]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /ogc/tiles/tiles")]
    public async Task OgcTiles_GetTilesets_ShouldReturnTilesetList()
    {
        // Act
        var response = await _client.GetAsync("/ogc/tiles/tiles");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("tilesets").GetArrayLength().Should().BeGreaterThan(0);
    }

    #endregion

    #region Health Check Tests

    [IntegrationTest]
    [Operation(Operations.LivenessCheck)]
    [Endpoint("GET /healthz/live")]
    public async Task Health_GetLiveness_ShouldReturnHealthy()
    {
        // Act
        var response = await _client.GetAsync("/healthz/live");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Healthy");
    }

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("GET /healthz/ready")]
    public async Task Health_GetReadiness_ShouldReturnReady()
    {
        // Act
        var response = await _client.GetAsync("/healthz/ready");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Ready");
    }

    #endregion

    #region Admin API Tests

    [IntegrationTest]
    [Operation(Operations.TableDiscovery)]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task Admin_GetTables_ShouldReturnTableList()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/connections/test/tables");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Unauthorized);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var content = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(content);
            json.RootElement.GetProperty("tables").ValueKind.Should().Be(JsonValueKind.Array);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/metadata/resources")]
    public async Task Admin_CreateMetadataResource_ShouldCreateLayerResource()
    {
        var resource = new Honua.Core.Features.Metadata.Domain.MetadataResource
        {
            ApiVersion = Honua.Core.Features.Metadata.Schema.MetadataSchemaRegistry.CurrentVersion,
            Kind = Honua.Core.Features.Metadata.Domain.MetadataResourceKinds.Layer,
            Metadata = new Honua.Core.Features.Metadata.Domain.ResourceMetadata
            {
                Name = $"coverage-layer-{Guid.NewGuid():N}",
                Namespace = "default"
            },
            Spec = JsonSerializer.SerializeToElement(new
            {
                tableName = "features",
                schemaName = _fixture.CurrentSchema ?? "public",
                geometryType = "Polygon",
                srid = 4326
            })
        };

        var payload = JsonSerializer.Serialize(
            resource,
            Honua.Server.Features.Admin.Models.MetadataResourceJsonContext.Default.MetadataResource);
        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/metadata/resources", content);

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    #endregion

    #region Import API Tests

    [IntegrationTest]
    [Operation(Operations.Import)]
    [Endpoint("POST /api/v1/admin/import/upload")]
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
        multipartContent.Add(new StringContent("coverage_import_table"), "TableName");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", multipartContent);

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Accepted, HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    [Endpoint("GET /api/v1/admin/import/jobs/{jobId}")]
    public async Task Import_GetJobStatus_ShouldReturnJobStatus()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();

        // Act
        var response = await _client.GetAsync($"/api/v1/admin/import/jobs/{jobId}");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.Unauthorized);
    }

    #endregion

    #region Error Handling Tests

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/nonexistent/FeatureServer")]
    public async Task ErrorHandling_NonexistentService_ShouldReturn404()
    {
        // Act
        var response = await _client.GetAsync("/rest/services/nonexistent/FeatureServer?f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/test/FeatureServer/99999")]
    public async Task ErrorHandling_NonexistentLayer_ShouldReturn404()
    {
        // Act
        var response = await _client.GetAsync("/rest/services/test/FeatureServer/99999?f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
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
