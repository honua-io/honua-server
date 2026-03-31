// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.CloudCog;

/// <summary>
/// Integration tests for cloud COG admin endpoints.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.CloudCogAdmin)]
public class CloudCogEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/cloud-rasters")]
    public async Task RegisterCloudCog_WithMissingName_Returns400()
    {
        // Arrange
        var request = new
        {
            layerId = 1,
            name = "",
            provider = "AwsS3",
            bucket = "test-bucket",
            objectKey = "test.tif"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/admin/cloud-rasters", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/cloud-rasters")]
    public async Task RegisterCloudCog_WithLocalProvider_Returns400()
    {
        // Arrange — Local provider is not valid for cloud COG serving
        var request = new
        {
            layerId = 1,
            name = "test-cog",
            provider = "Local",
            bucket = "test-bucket",
            objectKey = "test.tif"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/admin/cloud-rasters", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/cloud-rasters")]
    public async Task ListCloudCogs_WithoutLayerId_Returns400()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/cloud-rasters");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/cloud-rasters/{id}")]
    public async Task GetCloudCog_WithNonexistentId_Returns404()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/cloud-rasters/99999");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/cloud-rasters/{id}")]
    public async Task DeleteCloudCog_WithNonexistentId_Returns404()
    {
        // Act
        var response = await _client.DeleteAsync("/api/v1/admin/cloud-rasters/99999");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/cloud-rasters/{id}/refresh")]
    public async Task RefreshCloudCog_WithNonexistentId_Returns404()
    {
        // Act
        var response = await _client.PostAsync("/api/v1/admin/cloud-rasters/99999/refresh", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/cloud-rasters")]
    public async Task CloudCogCrud_RegisterListGetDelete_Lifecycle()
    {
        // Arrange — register a cloud COG for layer 1 (seeded in server.yaml)
        var request = new
        {
            layerId = 1,
            name = "lifecycle-test-cog",
            description = "Integration test COG",
            provider = "AwsS3",
            bucket = "test-bucket",
            objectKey = "lifecycle-test.tif"
        };

        // Act — Register
        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/cloud-rasters", request);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var createDoc = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("id").GetInt64();
        id.Should().BeGreaterThan(0);
        createDoc.RootElement.GetProperty("name").GetString().Should().Be("lifecycle-test-cog");
        createDoc.RootElement.GetProperty("provider").GetString().Should().Be("AwsS3");

        // Act — List by layer
        var listResponse = await _client.GetAsync("/api/v1/admin/cloud-rasters?layerId=1");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        listDoc.RootElement.GetArrayLength().Should().BeGreaterOrEqualTo(1);
        listDoc.RootElement.EnumerateArray()
            .Should().Contain(e => e.GetProperty("id").GetInt64() == id);

        // Act — Get by ID
        var getResponse = await _client.GetAsync($"/api/v1/admin/cloud-rasters/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var getDoc = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        getDoc.RootElement.GetProperty("id").GetInt64().Should().Be(id);
        getDoc.RootElement.GetProperty("objectKey").GetString().Should().Be("lifecycle-test.tif");

        // Act — Delete
        var deleteResponse = await _client.DeleteAsync($"/api/v1/admin/cloud-rasters/{id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Act — Verify deleted
        var getAfterDelete = await _client.GetAsync($"/api/v1/admin/cloud-rasters/{id}");
        getAfterDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/cloud-rasters")]
    public async Task RegisterCloudCog_DuplicateObject_Returns409()
    {
        // Arrange — register a cloud COG
        var request = new
        {
            layerId = 1,
            name = "duplicate-test-cog",
            provider = "AwsS3",
            bucket = "dup-test-bucket",
            objectKey = "duplicate-test.tif"
        };

        var firstResponse = await _client.PostAsJsonAsync("/api/v1/admin/cloud-rasters", request);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        // Act — attempt to register the same object again
        var duplicateRequest = new
        {
            layerId = 2,
            name = "duplicate-test-cog-2",
            provider = "AwsS3",
            bucket = "dup-test-bucket",
            objectKey = "duplicate-test.tif"
        };
        var secondResponse = await _client.PostAsJsonAsync("/api/v1/admin/cloud-rasters", duplicateRequest);

        // Assert — should return 409 Conflict, not 500
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Cleanup
        using var doc = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("id").GetInt64();
        await _client.DeleteAsync($"/api/v1/admin/cloud-rasters/{id}");
    }
}
