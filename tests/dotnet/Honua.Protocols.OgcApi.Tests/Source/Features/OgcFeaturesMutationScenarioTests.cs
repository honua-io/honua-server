// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Api.Features.Models;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

/// <summary>
/// Certification-depth mutation scenarios. Each xUnit test instance owns a fresh
/// database schema so a write can never satisfy another test's read-back.
/// </summary>
[Protocol(TestProtocols.OgcApiFeatures)]
[Collection("Database")]
public sealed class OgcFeaturesMutationScenarioTests : IAsyncLifetime
{
    private const int TestLayerId = 0;
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Community);

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Create, Operations.Update, Operations.Delete)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    [Endpoint("PATCH /ogc/features/collections/{collectionId}/items/{featureId}")]
    [Endpoint("DELETE /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task MutationLifecycle_CreateReplacePatchDelete_RoundTripsEachState()
    {
        var createResponse = await PostGeoJsonAsync(
            $"/ogc/features/collections/{TestLayerId}/items",
            Feature("mutation-created", -122.41, 37.77));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await DeserializeFeatureAsync(createResponse);
        created.Id.Should().NotBeNull();
        var featureId = Convert.ToInt64(created.Id, CultureInfo.InvariantCulture);
        (await ReadFeatureAsync(featureId)).Properties["name"]!.ToString().Should().Be("mutation-created");

        var replaceResponse = await PutGeoJsonAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{featureId}",
            Feature("mutation-replaced", -122.42, 37.78, featureId));
        replaceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadFeatureAsync(featureId)).Properties["name"]!.ToString().Should().Be("mutation-replaced");

        using var patchRequest = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/ogc/features/collections/{TestLayerId}/items/{featureId}")
        {
            Content = new StringContent(
                """{"properties":{"name":"mutation-patched"}}""",
                Encoding.UTF8,
                "application/merge-patch+json")
        };
        var patchResponse = await _fixture.Client.SendAsync(patchRequest);
        patchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var patched = await ReadFeatureAsync(featureId);
        patched.Properties["name"]!.ToString().Should().Be("mutation-patched");
        patched.Geometry.Should().NotBeNull("a properties-only patch must preserve geometry");

        var deleteResponse = await _fixture.Client.DeleteAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{featureId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var deletedRead = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{featureId}");
        deletedRead.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    [Endpoint("PATCH /ogc/features/collections/{collectionId}/items/{featureId}")]
    [Endpoint("DELETE /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task InvalidMutations_AreRejectedWithoutChangingStoredState()
    {
        var existingId = await _fixture.InsertFeatureAsync(TestLayerId, "mutation-original");
        var beforeCount = await ReadNumberMatchedAsync();

        using var invalidCreate = new StringContent(
            """{"type":"Feature","properties":{"name":"invalid-create"},"geometry":null}""",
            Encoding.UTF8,
            "text/plain");
        var createResponse = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{TestLayerId}/items",
            invalidCreate);
        createResponse.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await ReadNumberMatchedAsync()).Should().Be(beforeCount);

        var replaceResponse = await PutGeoJsonAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}",
            Feature("invalid-replace", -122.42, 37.78, existingId + 1));
        replaceResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadFeatureAsync(existingId)).Properties["name"]!.ToString().Should().Be("mutation-original");

        using var patchRequest = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/ogc/features/collections/{TestLayerId}/items/{existingId}")
        {
            Content = new StringContent(
                """{"properties":"not-an-object"}""",
                Encoding.UTF8,
                "application/merge-patch+json")
        };
        var patchResponse = await _fixture.Client.SendAsync(patchRequest);
        patchResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadFeatureAsync(existingId)).Properties["name"]!.ToString().Should().Be("mutation-original");

        var deleteResponse = await _fixture.Client.DeleteAsync(
            $"/ogc/features/collections/{TestLayerId}/items/999999999");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadFeatureAsync(existingId)).Properties["name"]!.ToString().Should().Be("mutation-original");
    }

    private async Task<HttpResponseMessage> PostGeoJsonAsync(string path, GeoJsonFeature feature)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(feature, OgcJsonContext.Default.GeoJsonFeature),
            Encoding.UTF8,
            MediaTypes.GeoJson);
        return await _fixture.Client.PostAsync(path, content);
    }

    private async Task<HttpResponseMessage> PutGeoJsonAsync(string path, GeoJsonFeature feature)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(feature, OgcJsonContext.Default.GeoJsonFeature),
            Encoding.UTF8,
            MediaTypes.GeoJson);
        return await _fixture.Client.PutAsync(path, content);
    }

    private async Task<GeoJsonFeature> ReadFeatureAsync(long featureId)
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{featureId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await DeserializeFeatureAsync(response);
    }

    private async Task<long> ReadNumberMatchedAsync()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items?limit=1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("numberMatched").GetInt64();
    }

    private static async Task<GeoJsonFeature> DeserializeFeatureAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize(body, OgcJsonContext.Default.GeoJsonFeature)
            ?? throw new InvalidOperationException("Expected a GeoJSON feature response.");
    }

    private static GeoJsonFeature Feature(string name, double x, double y, long? id = null) => new()
    {
        Type = "Feature",
        Id = id,
        Geometry = new SimpleGeoJsonGeometry
        {
            Type = "Point",
            CoordinatesJson = FormattableString.Invariant($"[{x}, {y}]")
        },
        Properties = new Dictionary<string, object?> { ["name"] = name }
    };
}
