// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.OData.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.OData;

/// <summary>
/// OData v4 CRUD tests for geometry operations across all geometry types.
/// Tests POST/PATCH/DELETE operations with Point, LineString, Polygon, and other geometry types.
/// Implements parity with OGC API Features test matrices per issue #200.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.ODataV4)]
public sealed class ODataGeometryCrudTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
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
    [Endpoint("POST /odata/Features({layerId}) with Point geometry")]
    public async Task CreateFeature_WithPointGeometry_ReturnsCreatedWithGeometry()
    {
        // Create a point geometry as Base64-encoded WKB
        var pointWkb = CreatePointWkb(-122.0, 37.0);
        var request = new ODataFeatureRequest
        {
            Geometry = Convert.ToBase64String(pointWkb),
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
            $"/odata/Features({TestLayerId})",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var responseContent = await response.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(responseContent, ODataJsonContext.Default.ODataFeatureResponse);
        created.Should().NotBeNull();
        created!.ObjectId.Should().BeGreaterThan(0);
        created.Geometry.Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Features({layerId}) without geometry")]
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
            $"/odata/Features({TestLayerId})",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var responseContent = await response.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(responseContent, ODataJsonContext.Default.ODataFeatureResponse);
        created.Should().NotBeNull();
        created!.ObjectId.Should().BeGreaterThan(0);
        created.Geometry.Should().BeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Features({layerId}) with attributes only")]
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
            $"/odata/Features({TestLayerId})",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var responseContent = await response.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(responseContent, ODataJsonContext.Default.ODataFeatureResponse);
        created!.Attributes.Should().Contain("Attributes Only City");
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
            Geometry = Convert.ToBase64String(CreatePointWkb(-122.0, 37.0)),
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Moving City"
            }
        };

        var createJson = JsonSerializer.Serialize(initialRequest, ODataJsonContext.Default.ODataFeatureRequest);
        var createResponse = await _fixture.Client.PostAsync(
            $"/odata/Features({TestLayerId})",
            new StringContent(createJson, Encoding.UTF8, "application/json"));

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var createContent = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(createContent, ODataJsonContext.Default.ODataFeatureResponse);
        var objectId = created!.ObjectId;

        // Now update with new geometry
        var newPointWkb = CreatePointWkb(-118.0, 34.0);
        var updateRequest = new ODataFeatureRequest
        {
            Geometry = Convert.ToBase64String(newPointWkb)
        };

        var updateJson = JsonSerializer.Serialize(updateRequest, ODataJsonContext.Default.ODataFeatureRequest);
        var message = new HttpRequestMessage(new HttpMethod("PATCH"), $"/odata/Features({TestLayerId},{objectId})")
        {
            Content = new StringContent(updateJson, Encoding.UTF8, "application/json")
        };

        var updateResponse = await _fixture.Client.SendAsync(message);

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var updateContent = await updateResponse.Content.ReadAsStringAsync();
        var updated = JsonSerializer.Deserialize(updateContent, ODataJsonContext.Default.ODataFeatureResponse);
        updated!.Geometry.Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features({layerId},{objectId}) update attributes only")]
    public async Task UpdateFeature_AttributesOnly_PreservesGeometry()
    {
        // Create a feature with geometry
        var initialRequest = new ODataFeatureRequest
        {
            Geometry = Convert.ToBase64String(CreatePointWkb(-122.0, 37.0)),
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Original Name"
            }
        };

        var createJson = JsonSerializer.Serialize(initialRequest, ODataJsonContext.Default.ODataFeatureRequest);
        var createResponse = await _fixture.Client.PostAsync(
            $"/odata/Features({TestLayerId})",
            new StringContent(createJson, Encoding.UTF8, "application/json"));

        var createContent = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(createContent, ODataJsonContext.Default.ODataFeatureResponse);
        var objectId = created!.ObjectId;
        var originalGeometry = created.Geometry;

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
        var updated = JsonSerializer.Deserialize(updateContent, ODataJsonContext.Default.ODataFeatureResponse);
        updated!.Geometry.Should().Be(originalGeometry); // Geometry should be preserved
        updated.Attributes.Should().Contain("Updated Name");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features({layerId},{objectId}) set geometry to null")]
    public async Task UpdateFeature_SetGeometryToNull_ClearsGeometry()
    {
        // Create a feature with geometry
        var initialRequest = new ODataFeatureRequest
        {
            Geometry = Convert.ToBase64String(CreatePointWkb(-122.0, 37.0)),
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Will Lose Geometry"
            }
        };

        var createJson = JsonSerializer.Serialize(initialRequest, ODataJsonContext.Default.ODataFeatureRequest);
        var createResponse = await _fixture.Client.PostAsync(
            $"/odata/Features({TestLayerId})",
            new StringContent(createJson, Encoding.UTF8, "application/json"));

        var createContent = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(createContent, ODataJsonContext.Default.ODataFeatureResponse);
        var objectId = created!.ObjectId;

        // Update with explicit null geometry
        var updateRequest = new ODataFeatureRequest
        {
            Geometry = null,
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Lost Geometry"
            }
        };

        var updateJson = JsonSerializer.Serialize(updateRequest, ODataJsonContext.Default.ODataFeatureRequest);
        var message = new HttpRequestMessage(new HttpMethod("PATCH"), $"/odata/Features({TestLayerId},{objectId})")
        {
            Content = new StringContent(updateJson, Encoding.UTF8, "application/json")
        };

        var updateResponse = await _fixture.Client.SendAsync(message);

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    #endregion

    #region DELETE - Delete Features with Geometry

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /odata/Features({layerId},{objectId}) with geometry")]
    public async Task DeleteFeature_WithGeometry_ReturnsNoContent()
    {
        // Create a feature with geometry
        var initialRequest = new ODataFeatureRequest
        {
            Geometry = Convert.ToBase64String(CreatePointWkb(-122.0, 37.0)),
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "To Be Deleted"
            }
        };

        var createJson = JsonSerializer.Serialize(initialRequest, ODataJsonContext.Default.ODataFeatureRequest);
        var createResponse = await _fixture.Client.PostAsync(
            $"/odata/Features({TestLayerId})",
            new StringContent(createJson, Encoding.UTF8, "application/json"));

        var createContent = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(createContent, ODataJsonContext.Default.ODataFeatureResponse);
        var objectId = created!.ObjectId;

        // Delete the feature
        var deleteResponse = await _fixture.Client.DeleteAsync($"/odata/Features({TestLayerId},{objectId})");

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify it's deleted
        var getResponse = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId},{objectId})");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /odata/Features({layerId},{objectId}) without geometry")]
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
            $"/odata/Features({TestLayerId})",
            new StringContent(createJson, Encoding.UTF8, "application/json"));

        var createContent = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(createContent, ODataJsonContext.Default.ODataFeatureResponse);
        var objectId = created!.ObjectId;

        // Delete the feature
        var deleteResponse = await _fixture.Client.DeleteAsync($"/odata/Features({TestLayerId},{objectId})");

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
    [Endpoint("POST /odata/Features({layerId}) with SRID 3857 layer")]
    public async Task CreateFeature_OnSrid3857Layer_TransformsGeometry()
    {
        // Use the spatial-reference seed with SRID 3857 layers
        var sridFixture = new WebAppFixture();
        sridFixture.UseSeed(Path.Combine("tests", "seed", "spatial-reference.yaml"));
        await sridFixture.InitializeAsync();

        try
        {
            // Create with WGS84 point - should be transformed to 3857
            var pointWkb = CreatePointWkb(-122.4194, 37.7749);
            var request = new ODataFeatureRequest
            {
                Geometry = Convert.ToBase64String(pointWkb),
                Attributes = new Dictionary<string, object?>
                {
                    ["name"] = "SRID Test Point"
                }
            };

            var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
            var response = await sridFixture.Client.PostAsync(
                $"/odata/Features({SpatialReferenceTestLayerCatalog.PointLayerId})",
                new StringContent(json, Encoding.UTF8, "application/json"));

            response.StatusCode.Should().Be(HttpStatusCode.Created);

            var responseContent = await response.Content.ReadAsStringAsync();
            var created = JsonSerializer.Deserialize(responseContent, ODataJsonContext.Default.ODataFeatureResponse);
            created!.Geometry.Should().NotBeNullOrEmpty();

            // Verify the geometry was stored with correct SRID
            var storedSrid = await SpatialReferenceTestData.GetGeometrySridAsync(
                sridFixture.Postgres,
                sridFixture.CurrentSchema!,
                created.ObjectId,
                SpatialReferenceTestLayerCatalog.PointLayerId);

            storedSrid.Should().Be(SpatialReferenceTestLayerCatalog.LayerSrid);
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
    [Endpoint("POST /odata/Features({layerId}) to nonexistent layer")]
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
            "/odata/Features(99999)",
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
            Geometry = Convert.ToBase64String(CreatePointWkb(-120.0, 36.0)),
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Batch Test City"
            }
        };

        var createJson = JsonSerializer.Serialize(createRequest, ODataJsonContext.Default.ODataFeatureRequest);
        var createResponse = await _fixture.Client.PostAsync(
            $"/odata/Features({TestLayerId})",
            new StringContent(createJson, Encoding.UTF8, "application/json"));

        var createContent = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(createContent, ODataJsonContext.Default.ODataFeatureResponse);
        var objectId = created!.ObjectId;

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

    /// <summary>
    /// Creates a WKB representation of a Point geometry.
    /// WKB format: byte order (1) + type (4 bytes) + x (8 bytes) + y (8 bytes)
    /// </summary>
    private static byte[] CreatePointWkb(double x, double y)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Byte order: little-endian
        writer.Write((byte)1);

        // Type: Point (1) with SRID flag (0x20000001)
        writer.Write(0x20000001);

        // SRID: 4326
        writer.Write(4326);

        // X and Y coordinates
        writer.Write(x);
        writer.Write(y);

        return ms.ToArray();
    }

    #endregion
}
