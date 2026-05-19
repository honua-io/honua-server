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

namespace Honua.Server.Tests.Features.Protocols.Zarr;

/// <summary>
/// Integration tests for Zarr admin endpoints (#1009).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Zarr)]
[Operation(Operations.ZarrAdmin)]
public class ZarrEndpointTests : IAsyncLifetime
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
    [Endpoint("POST /api/v1/admin/zarr-stores")]
    public async Task RegisterZarr_WithMissingName_Returns400()
    {
        var request = new
        {
            layerId = 1,
            name = string.Empty,
            provider = "AwsS3",
            bucket = "test-bucket",
            rootPath = "datasets/example"
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/zarr-stores", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/zarr-stores")]
    public async Task RegisterZarr_WithTraversalRootPath_Returns400()
    {
        var request = new
        {
            layerId = 1,
            name = "bad-path",
            provider = "AwsS3",
            bucket = "test-bucket",
            rootPath = "../etc/passwd"
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/zarr-stores", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/zarr-stores")]
    public async Task RegisterZarr_WithMissingLayer_Returns404()
    {
        var request = new
        {
            layerId = 99999,
            name = "missing-layer-zarr",
            provider = "AwsS3",
            bucket = "test-bucket",
            rootPath = "missing-layer.zarr"
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/zarr-stores", request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/zarr-stores")]
    public async Task ListZarr_WithoutLayerId_Returns400()
    {
        var response = await _client.GetAsync("/api/v1/admin/zarr-stores");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/zarr-stores/{id}")]
    public async Task GetZarr_WithNonexistentId_Returns404()
    {
        var response = await _client.GetAsync("/api/v1/admin/zarr-stores/99999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/zarr-stores/{id}")]
    public async Task DeleteZarr_WithNonexistentId_Returns404()
    {
        var response = await _client.DeleteAsync("/api/v1/admin/zarr-stores/99999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/zarr-stores/{id}/refresh")]
    public async Task RefreshZarr_WithNonexistentId_Returns404()
    {
        var response = await _client.PostAsync("/api/v1/admin/zarr-stores/99999/refresh", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/zarr-stores")]
    public async Task ZarrCrud_RegisterListGetDelete_Lifecycle()
    {
        var request = new
        {
            layerId = 1,
            name = "lifecycle-test-zarr",
            description = "Integration test Zarr store",
            provider = "AwsS3",
            bucket = "test-bucket",
            rootPath = "datasets/lifecycle.zarr"
        };

        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/zarr-stores", request);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var createDoc = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("id").GetInt64();
        id.Should().BeGreaterThan(0);
        createDoc.RootElement.GetProperty("name").GetString().Should().Be("lifecycle-test-zarr");
        createDoc.RootElement.GetProperty("rootPath").GetString().Should().Be("datasets/lifecycle.zarr");

        var listResponse = await _client.GetAsync("/api/v1/admin/zarr-stores?layerId=1");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        listDoc.RootElement.GetArrayLength().Should().BeGreaterOrEqualTo(1);
        listDoc.RootElement.EnumerateArray()
            .Should().Contain(e => e.GetProperty("id").GetInt64() == id);

        var getResponse = await _client.GetAsync($"/api/v1/admin/zarr-stores/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var deleteResponse = await _client.DeleteAsync($"/api/v1/admin/zarr-stores/{id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterDeleteResponse = await _client.GetAsync($"/api/v1/admin/zarr-stores/{id}");
        afterDeleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
