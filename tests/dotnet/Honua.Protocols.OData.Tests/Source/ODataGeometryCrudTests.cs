// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.OData.Models;
using Honua.TestKit;
using Honua.TestKit.Helpers;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Honua.Core.Features.Licensing.Domain;

namespace Honua.Server.Tests.Features.Protocols.OData;

/// <summary>
/// OData v4 CRUD tests for geometry operations across all geometry types.
/// Tests POST/PATCH/DELETE operations with Point, LineString, Polygon, and other geometry types.
/// Implements parity with OGC API Features test matrices per issue #200.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataGeometryCrudTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region POST - Create with Point Geometry

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features with Point geometry")]
    public async Task CreateFeature_WithPointGeometry_ReturnsCreatedWithGeometry()
    {
        var pointGeometry = CreatePointGeometry(-122.0, 37.0);
        var request = new ODataFeatureRequest
        {
            Geometry = pointGeometry,
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Test City",
                ["population"] = 100000L,
                ["is_capital"] = false,
                ["state"] = "California",
                ["country"] = "USA"
            }
        };

        var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
        var response = await _fixture.Client.PostAsync(
            $"/odata/Layers({TestLayerId})/Features",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);
        var created = document.RootElement;
        created.GetProperty("ObjectId").GetInt64().Should().BeGreaterThan(0);
        created.GetProperty("Geometry").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features without geometry")]
    public async Task CreateFeature_WithoutGeometry_ReturnsCreatedWithNullGeometry()
    {
        var request = new ODataFeatureRequest
        {
            Geometry = null,
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Virtual Test City",
                ["population"] = 0L,
                ["is_capital"] = false
            }
        };

        var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
        var response = await _fixture.Client.PostAsync(
            $"/odata/Layers({TestLayerId})/Features",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);
        var created = document.RootElement;
        created.GetProperty("ObjectId").GetInt64().Should().BeGreaterThan(0);
        created.GetProperty("Geometry").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features with attributes only")]
    public async Task CreateFeature_WithAttributesOnly_ReturnsCreated()
    {
        var request = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Attributes Only City",
                ["population"] = 50000L
            }
        };

        var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
        var response = await _fixture.Client.PostAsync(
            $"/odata/Layers({TestLayerId})/Features",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);
        var created = document.RootElement;
        var createdAttributes = ODataTestHelpers.ParseAttributes(created);
        createdAttributes.GetProperty("name").GetString().Should().Be("Attributes Only City");
    }

    #endregion

    #region PATCH - Update with Geometry

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features({layerId},{objectId}) update geometry")]
    public async Task UpdateFeature_WithNewGeometry_ReturnsUpdatedGeometry()
    {
        // First create a feature
        var initialRequest = new ODataFeatureRequest
        {
            Geometry = CreatePointGeometry(-122.0, 37.0),
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Moving City"
            }
        };

        var createJson = JsonSerializer.Serialize(initialRequest, ODataJsonContext.Default.ODataFeatureRequest);
        var createResponse = await _fixture.Client.PostAsync(
            $"/odata/Layers({TestLayerId})/Features",
            new StringContent(createJson, Encoding.UTF8, "application/json"));

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var createContent = await createResponse.Content.ReadAsStringAsync();
        using var createDocument = JsonDocument.Parse(createContent);
        var objectId = createDocument.RootElement.GetProperty("ObjectId").GetInt64();

        // Now update with new geometry
        var updateRequest = new ODataFeatureRequest
        {
            Geometry = CreatePointGeometry(-118.0, 34.0)
        };

        var updateJson = JsonSerializer.Serialize(updateRequest, ODataJsonContext.Default.ODataFeatureRequest);
        var message = new HttpRequestMessage(new HttpMethod("PATCH"), $"/odata/Features({TestLayerId},{objectId})")
        {
            Content = new StringContent(updateJson, Encoding.UTF8, "application/json")
        };

        var updateResponse = await _fixture.Client.SendAsync(message);

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var updateContent = await updateResponse.Content.ReadAsStringAsync();
        using var updateDocument = JsonDocument.Parse(updateContent);
        updateDocument.RootElement.GetProperty("Geometry").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features({layerId},{objectId}) update attributes only")]
    public async Task UpdateFeature_AttributesOnly_PreservesGeometry()
    {
        // Create a feature with geometry
        var initialRequest = new ODataFeatureRequest
        {
            Geometry = CreatePointGeometry(-122.0, 37.0),
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Original Name"
            }
        };

        var createJson = JsonSerializer.Serialize(initialRequest, ODataJsonContext.Default.ODataFeatureRequest);
        var createResponse = await _fixture.Client.PostAsync(
            $"/odata/Layers({TestLayerId})/Features",
            new StringContent(createJson, Encoding.UTF8, "application/json"));

        var createContent = await createResponse.Content.ReadAsStringAsync();
        using var createDocument = JsonDocument.Parse(createContent);
        var createRoot = createDocument.RootElement;
        var objectId = createRoot.GetProperty("ObjectId").GetInt64();
        var originalGeometry = createRoot.GetProperty("Geometry").GetRawText();

        // Update only attributes
        var updateRequest = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Updated Name"
            }
        };

        var updateJson = JsonSerializer.Serialize(updateRequest, ODataJsonContext.Default.ODataFeatureRequest);
        var message = new HttpRequestMessage(new HttpMethod("PATCH"), $"/odata/Features({TestLayerId},{objectId})")
        {
            Content = new StringContent(updateJson, Encoding.UTF8, "application/json")
        };

        var updateResponse = await _fixture.Client.SendAsync(message);

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var updateContent = await updateResponse.Content.ReadAsStringAsync();
        using var updateDocument = JsonDocument.Parse(updateContent);
        var updateRoot = updateDocument.RootElement;
        updateRoot.GetProperty("Geometry").GetRawText().Should().Be(originalGeometry);
        var updatedAttributes = ODataTestHelpers.ParseAttributes(updateRoot);
        updatedAttributes.GetProperty("name").GetString().Should().Be("Updated Name");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features({layerId},{objectId}) set geometry to null")]
    public async Task UpdateFeature_SetGeometryToNull_ClearsGeometry()
    {
        // Create a feature with geometry
        var initialRequest = new ODataFeatureRequest
        {
            Geometry = CreatePointGeometry(-122.0, 37.0),
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Will Lose Geometry"
            }
        };

        var createJson = JsonSerializer.Serialize(initialRequest, ODataJsonContext.Default.ODataFeatureRequest);
        var createResponse = await _fixture.Client.PostAsync(
            $"/odata/Layers({TestLayerId})/Features",
            new StringContent(createJson, Encoding.UTF8, "application/json"));

        var createContent = await createResponse.Content.ReadAsStringAsync();
        using var createDocument = JsonDocument.Parse(createContent);
        var objectId = createDocument.RootElement.GetProperty("ObjectId").GetInt64();

        // Source-generated serialization omits nulls, so use raw JSON to exercise an explicit Geometry:null PATCH.
        const string updateJson = """
            {"Geometry":null,"Attributes":{"name":"Lost Geometry"}}
            """;
        var message = new HttpRequestMessage(new HttpMethod("PATCH"), $"/odata/Features({TestLayerId},{objectId})")
        {
            Content = new StringContent(updateJson, Encoding.UTF8, "application/json")
        };

        var updateResponse = await _fixture.Client.SendAsync(message);

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var updateContent = await updateResponse.Content.ReadAsStringAsync();
        using var updateDocument = JsonDocument.Parse(updateContent);
        updateDocument.RootElement.GetProperty("Geometry").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /odata/Features({layerId},{objectId})")]
    public async Task ReplaceFeature_AttributesOnly_ClearsGeometry()
    {
        var initialRequest = new ODataFeatureRequest
        {
            Geometry = CreatePointGeometry(-122.0, 37.0),
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Will Be Replaced"
            }
        };

        var createJson = JsonSerializer.Serialize(initialRequest, ODataJsonContext.Default.ODataFeatureRequest);
        var createResponse = await _fixture.Client.PostAsync(
            $"/odata/Layers({TestLayerId})/Features",
            new StringContent(createJson, Encoding.UTF8, "application/json"));

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var createContent = await createResponse.Content.ReadAsStringAsync();
        using var createDocument = JsonDocument.Parse(createContent);
        var objectId = createDocument.RootElement.GetProperty("ObjectId").GetInt64();

        const string replaceJson = """
            {"Attributes":{"name":"Replaced"}}
            """;
        var message = new HttpRequestMessage(HttpMethod.Put, $"/odata/Features({TestLayerId},{objectId})")
        {
            Content = new StringContent(replaceJson, Encoding.UTF8, "application/json")
        };

        var replaceResponse = await _fixture.Client.SendAsync(message);

        replaceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var replaceContent = await replaceResponse.Content.ReadAsStringAsync();
        using var replaceDocument = JsonDocument.Parse(replaceContent);
        var replaceRoot = replaceDocument.RootElement;
        replaceRoot.GetProperty("Geometry").ValueKind.Should().Be(JsonValueKind.Null);
        var attributes = ODataTestHelpers.ParseAttributes(replaceRoot);
        attributes.GetProperty("name").GetString().Should().Be("Replaced");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /odata/Layers({layerId})/Features({objectId})")]
    public async Task ReplaceFeature_NestedLayerRoute_ReturnsUpdatedFeature()
    {
        var objectId = await _fixture.InsertFeatureAsync(TestLayerId, "Nested PUT Candidate");
        const string replaceJson = """
            {"Attributes":{"name":"Nested PUT Updated"}}
            """;
        var message = new HttpRequestMessage(HttpMethod.Put, $"/odata/Layers({TestLayerId})/Features({objectId})")
        {
            Content = new StringContent(replaceJson, Encoding.UTF8, "application/json")
        };

        var response = await _fixture.Client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var attributes = ODataTestHelpers.ParseAttributes(document.RootElement);
        attributes.GetProperty("name").GetString().Should().Be("Nested PUT Updated");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task ReplaceFeature_NamedKeyRoute_ReturnsUpdatedFeature()
    {
        var objectId = await _fixture.InsertFeatureAsync(TestLayerId, "Named PUT Candidate");
        const string replaceJson = """
            {"Attributes":{"name":"Named PUT Updated"}}
            """;
        var message = new HttpRequestMessage(HttpMethod.Put, $"/odata/Features(LayerId={TestLayerId},ObjectId={objectId})")
        {
            Content = new StringContent(replaceJson, Encoding.UTF8, "application/json")
        };

        var response = await _fixture.Client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var attributes = ODataTestHelpers.ParseAttributes(document.RootElement);
        attributes.GetProperty("name").GetString().Should().Be("Named PUT Updated");
    }

    #endregion

    #region DELETE - Delete Features with Geometry

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /odata/Features({layerId},{objectId})")]
    public async Task DeleteFeature_WithGeometry_ReturnsNoContent()
    {
        // Create a feature with geometry
        var initialRequest = new ODataFeatureRequest
        {
            Geometry = CreatePointGeometry(-122.0, 37.0),
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "To Be Deleted"
            }
        };

        var createJson = JsonSerializer.Serialize(initialRequest, ODataJsonContext.Default.ODataFeatureRequest);
        var createResponse = await _fixture.Client.PostAsync(
            $"/odata/Layers({TestLayerId})/Features",
            new StringContent(createJson, Encoding.UTF8, "application/json"));

        var createContent = await createResponse.Content.ReadAsStringAsync();
        using var createDocument = JsonDocument.Parse(createContent);
        var objectId = createDocument.RootElement.GetProperty("ObjectId").GetInt64();

        // Delete the feature
        var deleteResponse = await _fixture.Client.DeleteAsync($"/odata/Features({TestLayerId},{objectId})");

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify it's deleted
        var getResponse = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId},{objectId})");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /odata/Layers({layerId})/Features({objectId})")]
    public async Task DeleteFeature_WithoutGeometry_ReturnsNoContent()
    {
        // Create a feature without geometry
        var initialRequest = new ODataFeatureRequest
        {
            Geometry = null,
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "No Geometry Delete"
            }
        };

        var createJson = JsonSerializer.Serialize(initialRequest, ODataJsonContext.Default.ODataFeatureRequest);
        var createResponse = await _fixture.Client.PostAsync(
            $"/odata/Layers({TestLayerId})/Features",
            new StringContent(createJson, Encoding.UTF8, "application/json"));

        var createContent = await createResponse.Content.ReadAsStringAsync();
        using var createDocument = JsonDocument.Parse(createContent);
        var objectId = createDocument.RootElement.GetProperty("ObjectId").GetInt64();

        // Delete the feature
        var deleteResponse = await _fixture.Client.DeleteAsync($"/odata/Layers({TestLayerId})/Features({objectId})");

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /odata/Features({layerId},{objectId}) nonexistent")]
    public async Task DeleteFeature_NonExistent_ReturnsNotFound()
    {
        var response = await _fixture.Client.DeleteAsync($"/odata/Features({TestLayerId},999999)");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion

    #region CRUD with SRID Transformation

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features with SRID 3857 layer")]
    public async Task CreateFeature_OnSrid3857Layer_MismatchedGeometryCrs_ReturnsBadRequest()
    {
        // Use the spatial-reference seed with SRID 3857 layers
        var sridFixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);
        sridFixture.UseSeed(Path.Combine("tests", "seed", "spatial-reference.yaml"));
        await sridFixture.InitializeAsync();

        // V2 ProcessGeometryAsync rejects geometry whose CRS SRID does not match the
        // resource's Spatial.SpatialReference.Srid; the default test V2 graph
        // (WebAppFixtureMetadataV2Mixin.BuildDefaultTestGraph) does not seed Spatial on the
        // spatial-reference fixture layers. Mirror the v1 SpatialReferenceTestLayerCatalog
        // (EPSG:3857 / Web Mercator) so the SRID mismatch path under test
        // (4326 geometry → 3857 layer) fires.
        var sridSpatial = new Honua.Core.Features.Metadata.Domain.V2.MetadataV2ResourceSpatial
        {
            SpatialReference = new Honua.Core.Features.Metadata.Domain.V2.MetadataV2SpatialReference
            {
                Srid = SpatialReferenceTestLayerCatalog.LayerSrid,
                Crs = $"EPSG:{SpatialReferenceTestLayerCatalog.LayerSrid}",
                IsGeographic = false,
            },
        };
        sridFixture.UpdateV2ResourceMetadata(
            SpatialReferenceTestLayerCatalog.PointLayerId,
            spatial: sridSpatial with { GeometryType = Honua.Core.Features.Metadata.Domain.V2.MetadataV2GeometryType.Point });

        try
        {
            // Create with WGS84 point - should be rejected without reprojection
            var request = new ODataFeatureRequest
            {
                Geometry = CreatePointGeometry(-122.4194, 37.7749, 4326),
                Attributes = new Dictionary<string, object?>
                {
                    ["name"] = "SRID Test Point"
                }
            };

            var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
            var response = await sridFixture.Client.PostAsync(
                $"/odata/Layers({SpatialReferenceTestLayerCatalog.PointLayerId})/Features",
                new StringContent(json, Encoding.UTF8, "application/json"));

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var responseContent = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(responseContent);
            var details = document.RootElement.GetProperty("error").GetProperty("details");
            details[0].GetProperty("message").GetString().Should().Contain("does not match layer SRID");
        }
        finally
        {
            await sridFixture.DisposeAsync();
        }
    }

    #endregion

    #region Error Cases

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features to nonexistent layer")]
    public async Task CreateFeature_NonExistentLayer_ReturnsNotFound()
    {
        var request = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Bad Layer"
            }
        };

        var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
        var response = await _fixture.Client.PostAsync(
            "/odata/Layers(99999)/Features",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features({layerId},{objectId}) nonexistent")]
    public async Task UpdateFeature_NonExistent_ReturnsNotFound()
    {
        var updateRequest = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Update Nothing"
            }
        };

        var updateJson = JsonSerializer.Serialize(updateRequest, ODataJsonContext.Default.ODataFeatureRequest);
        var message = new HttpRequestMessage(new HttpMethod("PATCH"), $"/odata/Features({TestLayerId},999999)")
        {
            Content = new StringContent(updateJson, Encoding.UTF8, "application/json")
        };

        var response = await _fixture.Client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion

    #region Batch CRUD with Geometry

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch with geometry operations")]
    public async Task Batch_CreateUpdateDelete_WithGeometry_ExecutesAll()
    {
        // First create a feature to work with
        var createRequest = new ODataFeatureRequest
        {
            Geometry = CreatePointGeometry(-120.0, 36.0),
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Batch Test City"
            }
        };

        var createJson = JsonSerializer.Serialize(createRequest, ODataJsonContext.Default.ODataFeatureRequest);
        var createResponse = await _fixture.Client.PostAsync(
            $"/odata/Layers({TestLayerId})/Features",
            new StringContent(createJson, Encoding.UTF8, "application/json"));

        var createContent = await createResponse.Content.ReadAsStringAsync();
        using var createDocument = JsonDocument.Parse(createContent);
        var objectId = createDocument.RootElement.GetProperty("ObjectId").GetInt64();

        // Create batch with update and then delete
        var batchRequest = new
        {
            requests = new object[]
            {
                new
                {
                    id = "1",
                    method = "PATCH",
                    url = $"Features({TestLayerId},{objectId})",
                    body = new
                    {
                        Attributes = new Dictionary<string, object?> { ["name"] = "Batch Updated" }
                    }
                },
                new
                {
                    id = "2",
                    method = "DELETE",
                    url = $"Features({TestLayerId},{objectId})"
                }
            }
        };

        var batchJson = JsonSerializer.Serialize(batchRequest);
        var batchResponse = await _fixture.Client.PostAsync(
            "/odata/$batch",
            new StringContent(batchJson, Encoding.UTF8, "application/json"));

        batchResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var batchContent = await batchResponse.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(batchContent);

        var responses = document.RootElement.GetProperty("responses");
        responses.GetArrayLength().Should().Be(2);
    }

    #endregion

    #region Helper Methods

    private static ODataSpatialGeometry CreatePointGeometry(double x, double y, int? srid = null)
    {
        return new ODataSpatialGeometry
        {
            Type = "Point",
            CoordinatesJson = FormattableString.Invariant($"[{x},{y}]"),
            Crs = srid.HasValue
                ? new ODataSpatialCrs
                {
                    Type = "name",
                    Properties = new ODataSpatialCrsProperties { Name = $"EPSG:{srid.Value}" }
                }
                : null
        };
    }

    #endregion
}
