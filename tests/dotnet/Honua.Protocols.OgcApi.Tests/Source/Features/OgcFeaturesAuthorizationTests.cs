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

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
public sealed class OgcFeaturesAuthorizationTests : IAsyncLifetime
{
    private const string AdminApiKey = "test-ogc-admin-key";
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminApiKey);
        });

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_WithoutApiKey_ReturnsUnauthorized()
    {
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items",
            CreateGeoJsonContent("Unauthorized Create"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task UpdateFeature_WithoutApiKey_ReturnsUnauthorized()
    {
        var response = await _fixture.Client.PutAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items/1",
            CreateGeoJsonContent("Unauthorized Update"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task DeleteFeature_WithoutApiKey_ReturnsUnauthorized()
    {
        var response = await _fixture.Client.DeleteAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items/1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.BulkCreate)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items/batch")]
    public async Task Batch_WithoutApiKey_ReturnsUnauthorized()
    {
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
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items/batch",
            new StringContent(content, Encoding.UTF8, MediaTypes.Json));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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
