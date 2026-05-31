// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Regression coverage for #1298: Esri clients (ArcGIS Pro, ArcGIS Python SDK)
// hydrate service/layer metadata by POSTing {"f":"json"} to the REST resource
// roots. Honua previously returned 405 on those POSTs, breaking SDK hydration.
// These tests assert each metadata root now accepts POST and returns the same
// payload as the GET form.

using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

/// <summary>
/// Verifies the GeoServices metadata endpoints accept POST (Esri SDK hydration
/// path) and return metadata identical to the GET form.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class MetadataPostTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static FormUrlEncodedContent EmptyJsonForm()
        => new(new[] { new KeyValuePair<string, string>("f", "json") });

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer")]
    public async Task FeatureServer_ServiceMetadata_Post_ReturnsSameAsGet()
    {
        var getResponse = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer?f=json");
        var postResponse = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer", EmptyJsonForm());

        postResponse.Be200Ok();
        postResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var getBody = await getResponse.Content.ReadAsStringAsync();
        var postBody = await postResponse.Content.ReadAsStringAsync();

        using var getDoc = JsonDocument.Parse(getBody);
        using var postDoc = JsonDocument.Parse(postBody);
        postDoc.RootElement.GetProperty("serviceName").GetString().Should().Be(TestServiceId);
        postDoc.RootElement.GetProperty("currentVersion").GetDouble().Should().BeGreaterThan(0);
        postBody.Should().Be(getBody);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task FeatureServer_LayerMetadata_Post_ReturnsSameAsGet()
    {
        var getResponse = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}?f=json");
        var postResponse = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}", EmptyJsonForm());

        postResponse.Be200Ok();
        postResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var getBody = await getResponse.Content.ReadAsStringAsync();
        var postBody = await postResponse.Content.ReadAsStringAsync();

        using var postDoc = JsonDocument.Parse(postBody);
        postDoc.RootElement.GetProperty("id").GetInt32().Should().Be(TestLayerId);
        postBody.Should().Be(getBody);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer")]
    public async Task MapServer_ServiceMetadata_Post_ReturnsSameAsGet()
    {
        var getResponse = await _fixture.Client.GetAsync($"/rest/services/{WebAppFixture.TestServiceId}/MapServer?f=json");
        var postResponse = await _fixture.Client.PostAsync($"/rest/services/{WebAppFixture.TestServiceId}/MapServer", EmptyJsonForm());

        var postBody = await postResponse.Content.ReadAsStringAsync();
        postResponse.StatusCode.Should().Be(HttpStatusCode.OK, postBody);
        postResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var getBody = await getResponse.Content.ReadAsStringAsync();
        using var postDoc = JsonDocument.Parse(postBody);
        postDoc.RootElement.GetProperty("mapName").GetString().Should().NotBeNullOrWhiteSpace();
        postBody.Should().Be(getBody);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/{layerId}")]
    public async Task MapServer_LayerMetadata_Post_ReturnsSameAsGet()
    {
        var getResponse = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}?f=json");
        var postResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}", EmptyJsonForm());

        var postBody = await postResponse.Content.ReadAsStringAsync();
        postResponse.StatusCode.Should().Be(HttpStatusCode.OK, postBody);
        postResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var getBody = await getResponse.Content.ReadAsStringAsync();
        using var postDoc = JsonDocument.Parse(postBody);
        postDoc.RootElement.GetProperty("id").GetInt32().Should().Be(WebAppFixture.TestLayerId);
        postBody.Should().Be(getBody);
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("POST /rest/services/{id}/ImageServer")]
    public async Task ImageServer_ServiceInfo_Post_MatchesGet()
    {
        var getResponse = await _fixture.Client.GetAsync($"/rest/services/{TestLayerId}/ImageServer?f=json");
        var postResponse = await _fixture.Client.PostAsync($"/rest/services/{TestLayerId}/ImageServer", EmptyJsonForm());

        // ImageServer may return 404 when no raster data is seeded; POST must
        // mirror GET (never 405).
        postResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
        postResponse.StatusCode.Should().Be(getResponse.StatusCode);

        if (postResponse.StatusCode == HttpStatusCode.OK)
        {
            var postBody = await postResponse.Content.ReadAsStringAsync();
            var getBody = await getResponse.Content.ReadAsStringAsync();
            postBody.Should().Be(getBody);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /rest/services/{serviceId}/ImageServer")]
    public async Task ImageServer_ServiceInfoByService_Post_MatchesGet()
    {
        var serviceId = WebAppFixture.TestServiceId;
        var getResponse = await _fixture.Client.GetAsync($"/rest/services/{serviceId}/ImageServer?f=json");
        var postResponse = await _fixture.Client.PostAsync($"/rest/services/{serviceId}/ImageServer", EmptyJsonForm());

        postResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
        postResponse.StatusCode.Should().Be(getResponse.StatusCode);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer")]
    public async Task GeometryServer_Info_Post_ReturnsSameAsGet()
    {
        var getResponse = await _fixture.Client.GetAsync("/rest/services/Utilities/Geometry/GeometryServer");
        var postResponse = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer", EmptyJsonForm());

        postResponse.Be200Ok();
        postResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var getBody = await getResponse.Content.ReadAsStringAsync();
        var postBody = await postResponse.Content.ReadAsStringAsync();
        using var postDoc = JsonDocument.Parse(postBody);
        postDoc.RootElement.TryGetProperty("currentVersion", out _).Should().BeTrue();
        postBody.Should().Be(getBody);
    }
}
