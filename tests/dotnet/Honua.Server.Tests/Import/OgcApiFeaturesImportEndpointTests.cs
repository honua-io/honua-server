// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for OGC API Features collection import endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class OgcApiFeaturesImportEndpointTests : IAsyncLifetime
{
    private static readonly double[] InvalidBboxThreeValues = { 1.0, 2.0, 3.0 };

    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/ogc-api-features/collection")]
    public async Task Collection_WithMissingServiceUrl_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/ogc-api-features/collection", new
        {
            CollectionId = "buildings",
            TargetSchema = "honua_data"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("ServiceUrl is required");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/ogc-api-features/collection")]
    public async Task Collection_WithEmptyFilter_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/ogc-api-features/collection", new
        {
            ServiceUrl = "https://example.com/ogcapi/",
            CollectionId = "buildings",
            TargetSchema = "honua_data",
            Filter = "   "
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Filter must be a non-empty");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/ogc-api-features/collection")]
    public async Task Collection_WithInvalidBboxLength_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/ogc-api-features/collection", new
        {
            ServiceUrl = "https://example.com/ogcapi/",
            CollectionId = "buildings",
            TargetSchema = "honua_data",
            Bbox = InvalidBboxThreeValues
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Bbox");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/ogc-api-features/collection")]
    public async Task Collection_WithInvalidDatetime_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/ogc-api-features/collection", new
        {
            ServiceUrl = "https://example.com/ogcapi/",
            CollectionId = "buildings",
            TargetSchema = "honua_data",
            Datetime = "   "
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Datetime");
    }
}
