// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Api.Features.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Protocol(TestProtocols.OgcApiFeatures)]
[Collection("Database")]
public sealed class OgcFeaturesAuthorizationTests : IClassFixture<OgcFeaturesAuthorizationTestsFixture>
{
    private const string AdminApiKey = "test-ogc-admin-key";
    private readonly WebAppFixture _fixture;

    public OgcFeaturesAuthorizationTests(OgcFeaturesAuthorizationTestsFixture fixture)
    {
        _fixture = fixture.App;
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_WithoutApiKey_ReturnsUnauthorized()
    {
        var before = await ReadAuthorizedStateAsync();
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items",
            CreateGeoJsonContent("Unauthorized Create"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertDeniedWithoutChangesAsync(response, before);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task UpdateFeature_WithoutApiKey_ReturnsUnauthorized()
    {
        var before = await ReadAuthorizedStateAsync();
        var response = await _fixture.Client.PutAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items/1",
            CreateGeoJsonContent("Unauthorized Update"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertDeniedWithoutChangesAsync(response, before);
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task DeleteFeature_WithoutApiKey_ReturnsUnauthorized()
    {
        var before = await ReadAuthorizedStateAsync();
        var response = await _fixture.Client.DeleteAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items/1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertDeniedWithoutChangesAsync(response, before);
    }

    [IntegrationTest]
    [Operation(Operations.BulkCreate)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items/batch")]
    public async Task Batch_WithoutApiKey_ReturnsUnauthorized()
    {
        var before = await ReadAuthorizedStateAsync();
        var batch = new BatchRequest
        {
            Operations =
            [
                new BatchOperation
                {
                    Id = "create-1",
                    Type = "CREATE",
                    Feature = CreatePointFeature("Unauthorized Batch")
                }
            ]
        };

        var content = JsonSerializer.Serialize(batch, OgcJsonContext.Default.BatchRequest);
        using var requestContent = new StringContent(content, Encoding.UTF8, MediaTypes.Json);
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items/batch",
            requestContent);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertDeniedWithoutChangesAsync(response, before);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_WithApiKey_ReturnsCreated()
    {
        using var client = _fixture.CreateClient(c =>
            c.DefaultRequestHeaders.Add("X-API-Key", AdminApiKey));

        var response = await client.PostAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items",
            CreateGeoJsonContent("Authorized Create"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
        using var readBack = await client.GetAsync(response.Headers.Location);
        readBack.StatusCode.Should().Be(HttpStatusCode.OK);
        using var created = JsonDocument.Parse(await readBack.Content.ReadAsStringAsync());
        created.RootElement.GetProperty("properties").GetProperty("name").GetString().Should().Be("Authorized Create");
        created.RootElement.GetProperty("geometry").GetProperty("coordinates").EnumerateArray()
            .Select(value => value.GetDouble()).Should().Equal(-122.4194, 37.7749);
    }

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithoutApiKey_RefusesAndDisclosesNoRecords()
    {
        var before = await ReadAuthorizedStateAsync();
        using var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{WebAppFixture.TestLayerId}/items");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertDeniedWithoutChangesAsync(response, before);
    }

    private async Task<(int Count, string Target)> ReadAuthorizedStateAsync()
    {
        using var client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminApiKey));
        using var list = await client.GetAsync($"/ogc/features/collections/{WebAppFixture.TestLayerId}/items?limit=1000");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var collection = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var count = collection.RootElement.GetProperty("features").GetArrayLength();
        count.Should().BeGreaterThanOrEqualTo(5, "the protected fixture must contain records");
        using var response = await client.GetAsync($"/ogc/features/collections/{WebAppFixture.TestLayerId}/items/1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var target = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        target.RootElement.GetProperty("properties").GetProperty("name").GetString().Should().Be("Test Feature");
        return (count, target.RootElement.GetProperty("properties").GetRawText() + target.RootElement.GetProperty("geometry").GetRawText());
    }

    private async Task AssertDeniedWithoutChangesAsync(HttpResponseMessage response, (int Count, string Target) before)
    {
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("Test Feature").And.NotContain("Unauthorized Create")
            .And.NotContain("Unauthorized Update").And.NotContain("Unauthorized Batch");
        using var error = JsonDocument.Parse(body);
        error.RootElement.TryGetProperty("features", out _).Should().BeFalse();
        error.RootElement.TryGetProperty("geometry", out _).Should().BeFalse();
        (await ReadAuthorizedStateAsync()).Should().Be(before, "a denied request must preserve row count and target attributes/geometry");
    }

    private static StringContent CreateGeoJsonContent(string name)
    {
        var feature = CreatePointFeature(name);
        var json = JsonSerializer.Serialize(feature, OgcJsonContext.Default.GeoJsonFeature);
        return new StringContent(json, Encoding.UTF8, MediaTypes.GeoJson);
    }

    private static GeoJsonFeature CreatePointFeature(string name)
    {
        return new GeoJsonFeature
        {
            Type = "Feature",
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[-122.4194, 37.7749]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["name"] = name
            }
        };
    }
}

public sealed class OgcFeaturesAuthorizationTestsFixture : IAsyncLifetime
{
    private const string AdminApiKey = "test-ogc-admin-key";

    public WebAppFixture App { get; } = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminApiKey);
        });

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();
        App.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, accessPolicy: new Honua.Core.Features.Security.Domain.AccessPolicy
        {
            AllowAnonymous = false,
            AllowAnonymousWrite = false
        });
    }

    public Task DisposeAsync() => App.DisposeAsync();
}
