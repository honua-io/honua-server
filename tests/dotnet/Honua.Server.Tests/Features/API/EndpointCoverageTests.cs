// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.OData.Models;
using Honua.TestKit;
using Honua.TestKit.Extensions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.API;

/// <summary>
/// Comprehensive API endpoint coverage tests ensuring 100% API surface testing
/// </summary>
[Collection("Database")]
public sealed class EndpointCoverageTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.EnableV2ServiceEditingCapabilities(WebAppFixture.TestServiceId, ["Query", "Create", "Update", "Delete"]);
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
        using var queryPayload = new FormUrlEncodedContent(new[]
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
        using var content = new StringContent(editPayload, Encoding.UTF8, "application/json");

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
        using var content = new StringContent(batchPayload, Encoding.UTF8, "application/json");

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
        // This fixture runs with the dev-auth bypass, so _client IS an authorized principal
        // and there is exactly one expected outcome. The previous OK-or-Unauthorized
        // disjunction was dead cover: the Unauthorized branch is unreachable here, and the
        // conditional body meant an endpoint that returned nothing still passed (#4390).

        // Act: "test" is the secure connection WebAppFixture registers against the fixture's
        // own PostGIS, so the discovery has a non-empty database to describe.
        var response = await _client.GetAsync("/api/v1/admin/connections/test/tables");

        // Assert
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var json = JsonDocument.Parse(payload);
        var tables = json.RootElement.GetProperty("tables");
        tables.ValueKind.Should().Be(JsonValueKind.Array);
        tables.GetArrayLength().Should().BeGreaterThan(
            0,
            "discovery against the seeded fixture database must return at least one spatial table");

        // Every entry is a real discovered table, not an empty placeholder object.
        foreach (var table in tables.EnumerateArray())
        {
            table.GetProperty("schema").GetString().Should().NotBeNullOrWhiteSpace();
            table.GetProperty("table").GetString().Should().NotBeNullOrWhiteSpace();
        }

        // ...and the discovery really is spatial discovery, not a bare table listing.
        var hasGeometryColumn = false;
        foreach (var table in tables.EnumerateArray())
        {
            if (table.TryGetProperty("geometryColumn", out var column) &&
                column.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(column.GetString()))
            {
                hasGeometryColumn = true;
                break;
            }
        }

        hasGeometryColumn.Should().BeTrue(
            "PostGIS discovery must report the geometry column of at least one seeded spatial table");

        // An unknown connection id is a distinct, pinned outcome — so "returns something for
        // everything" cannot pass as a table list.
        var unknown = await _client.GetAsync("/api/v1/admin/connections/no-such-connection-4390/tables");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // v1 admin metadata-resource POST endpoint removed in #1035 cutover; V2 admin UX (epic #1046) tracks the replacement.

    #endregion

    #region Import API Tests

    [IntegrationTest]
    [Operation(Operations.Import)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Import_UploadFile_ShouldAcceptValidFormats()
    {
        // This fixture runs with the dev-auth bypass, so _client IS an authorized principal
        // and the upload has exactly one expected outcome. The previous
        // OK-or-Accepted-or-Unauthorized disjunction asserted nothing about whether anything
        // was actually imported (#4390).

        // Arrange
        var tableName = $"coverage_import_{Guid.NewGuid():N}"[..30];
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

        using var multipartContent = new MultipartFormDataContent();
        multipartContent.Add(new StringContent(geoJsonContent), "file", "test.geojson");
        multipartContent.Add(new StringContent(tableName), "TableName");
        multipartContent.Add(new StringContent("4326"), "TargetSrid");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", multipartContent);

        // Assert: a synchronous upload completes inline with 200 and a real ImportResult.
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);

        using var result = JsonDocument.Parse(payload);
        result.RootElement.GetProperty("success").GetBoolean().Should().BeTrue(payload);
        result.RootElement.GetProperty("tableName").GetString().Should().Be(tableName);
        result.RootElement.GetProperty("format").GetString().Should().Be("GeoJson");
        result.RootElement.GetProperty("featureCount").GetInt32().Should().Be(
            1,
            "the posted FeatureCollection carries exactly one feature, so an import that silently dropped it must fail");
        result.RootElement.GetProperty("detectedSrid").GetInt32().Should().Be(4326);

        // The import really landed: the physical table is discoverable through the same
        // connection the admin table-discovery endpoint reads.
        var physicalTableName = result.RootElement.GetProperty("physicalTableName").GetString();
        physicalTableName.Should().NotBeNullOrWhiteSpace();

        var discovery = await _client.GetAsync("/api/v1/admin/connections/test/tables");
        var discoveryPayload = await discovery.Content.ReadAsStringAsync();
        discovery.StatusCode.Should().Be(HttpStatusCode.OK, discoveryPayload);
        discoveryPayload.Should().Contain(
            physicalTableName!,
            "an upload that reported success must have created a table the server can discover");
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    [Endpoint("GET /api/v1/admin/import/jobs/{jobId}")]
    public async Task Import_GetJobStatus_ShouldReturnJobStatus()
    {
        // The previous OK-or-NotFound-or-Unauthorized disjunction could not tell a job
        // document from a not-found envelope, so it passed whatever the endpoint did (#4390).
        // Both outcomes are now pinned separately, on the same authorized principal.

        // 1. An id that was never queued is exactly 404, and discloses no job document.
        var unknownJobId = Guid.NewGuid().ToString();

        var unknown = await _client.GetAsync($"/api/v1/admin/import/jobs/{unknownJobId}");

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var unknownBody = await unknown.Content.ReadAsStringAsync();
        unknownBody.Should().NotContain("\"currentPhase\"");
        unknownBody.Should().NotContain("\"tableName\"");

        // 2. A really queued background import returns ITS status, keyed by its own id.
        var tableName = $"coverage_jobstatus_{Guid.NewGuid():N}"[..30];
        var geoJsonContent = """
            {
                "type": "FeatureCollection",
                "features": [
                    {
                        "type": "Feature",
                        "geometry": { "type": "Point", "coordinates": [-122.0, 37.0] },
                        "properties": { "name": "Job Status Point" }
                    }
                ]
            }
            """;

        using var multipartContent = new MultipartFormDataContent();
        var fileContent = new StringContent(geoJsonContent, Encoding.UTF8, "application/json");
        fileContent.Headers.ContentDisposition =
            new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
            {
                Name = "File",
                FileName = "job-status.geojson",
            };
        multipartContent.Add(fileContent);
        multipartContent.Add(new StringContent(tableName), "TableName");
        multipartContent.Add(new StringContent("true"), "ForceBackground");

        var queued = await _client.PostAsync("/api/v1/admin/import/upload", multipartContent);
        var queuedPayload = await queued.Content.ReadAsStringAsync();
        queued.StatusCode.Should().Be(HttpStatusCode.Accepted, queuedPayload);

        using var queuedJson = JsonDocument.Parse(queuedPayload);
        var jobId = queuedJson.RootElement.GetProperty("jobId").GetString();
        jobId.Should().NotBeNullOrWhiteSpace();

        var response = await _client.GetAsync($"/api/v1/admin/import/jobs/{jobId}");

        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);

        using var job = JsonDocument.Parse(payload);
        job.RootElement.GetProperty("jobId").GetString().Should().Be(jobId);
        job.RootElement.GetProperty("tableName").GetString().Should().Be(
            tableName,
            "the status must describe the job that was queued, not an arbitrary job");
        job.RootElement.GetProperty("fileName").GetString().Should().Be("job-status.geojson");
        job.RootElement.GetProperty("currentPhase").GetString().Should().NotBeNullOrWhiteSpace();
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

        // Assert (PA-070/PA-117 #2418: GeoServices errors are HTTP 200 + {"error":{"code":404}})
        await response.AssertGeoServicesErrorAsync(404);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/test/FeatureServer/99999")]
    public async Task ErrorHandling_NonexistentLayer_ShouldReturn404()
    {
        // Act
        var response = await _client.GetAsync("/rest/services/test/FeatureServer/99999?f=json");

        // Assert (PA-070/PA-117 #2418: GeoServices errors are HTTP 200 + {"error":{"code":404}})
        await response.AssertGeoServicesErrorAsync(404);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/test/FeatureServer/1/query?where=invalid")]
    public async Task ErrorHandling_InvalidQuery_ShouldReturn400()
    {
        // Act
        var response = await _client.GetAsync("/rest/services/test/FeatureServer/1/query?where=INVALID_SQL_SYNTAX&f=json");

        // Assert (PA-070/PA-117 #2418: GeoServices errors are HTTP 200 + {"error":{"code":400}})
        await response.AssertGeoServicesErrorAsync(400);
    }

    #endregion
}
