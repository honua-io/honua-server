// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.Ogc.Api.Features;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Protocol(TestProtocols.OgcApiFeatures)]
[Collection("Database")]
public sealed class OgcFeaturesFieldClientConcurrencyTests : IClassFixture<OgcFeaturesTransactionTestsFixture>
{
    private readonly WebAppFixture _fixture;

    public OgcFeaturesFieldClientConcurrencyTests(OgcFeaturesTransactionTestsFixture fixture)
    {
        _fixture = fixture.App;
    }

    [IntegrationTheory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [Operation(Operations.Update)]
    [Operation(Operations.Delete)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    [Endpoint("PATCH /ogc/features/collections/{collectionId}/items/{featureId}")]
    [Endpoint("DELETE /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task MutateFeature_OfflineClientUsesStaleVersion_PreservesOnlineEdit(string method)
    {
        var id = await _fixture.InsertFeatureAsync(0, "Original field observation");
        var itemPath = $"/ogc/features/collections/0/items/{id}";
        using var original = await _fixture.Client.GetAsync(itemPath);
        original.StatusCode.Should().Be(HttpStatusCode.OK);
        original.Headers.ETag.Should().NotBeNull();
        var staleTag = original.Headers.ETag!.ToString();

        using var onlineEdit = BuildMutation("PATCH", itemPath);
        onlineEdit.Headers.TryAddWithoutValidation("If-Match", staleTag);
        using var onlineResult = await _fixture.Client.SendAsync(onlineEdit);
        onlineResult.StatusCode.Should().Be(HttpStatusCode.OK);

        using var winner = await _fixture.Client.GetAsync(itemPath);
        winner.StatusCode.Should().Be(HttpStatusCode.OK);
        winner.Headers.ETag.Should().NotBeNull();
        winner.Headers.ETag!.ToString().Should().NotBe(staleTag);
        var winningBody = await winner.Content.ReadAsStringAsync();

        using var offlineEdit = BuildMutation(method, itemPath);
        offlineEdit.Headers.TryAddWithoutValidation("If-Match", staleTag);
        using var rejected = await _fixture.Client.SendAsync(offlineEdit);
        rejected.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);

        using var current = await _fixture.Client.GetAsync(itemPath);
        current.StatusCode.Should().Be(HttpStatusCode.OK);
        current.Headers.ETag.Should().Be(winner.Headers.ETag);
        var currentBody = await current.Content.ReadAsStringAsync();
        var feature = JsonSerializer.Deserialize(currentBody, OgcJsonContext.Default.GeoJsonFeature);
        feature!.Properties["name"]!.ToString().Should().Be("Online observation");
        currentBody.Should().Be(winningBody);
    }

    private static HttpRequestMessage BuildMutation(string method, string itemPath)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), itemPath);
        if (method == "PATCH")
        {
            request.Content = new StringContent(
                """{"properties":{"name":"Online observation"}}""",
                Encoding.UTF8, "application/merge-patch+json");
        }
        else if (method == "PUT")
        {
            request.Content = new StringContent(
                """{"type":"Feature","geometry":null,"properties":{"name":"Offline observation"}}""",
                Encoding.UTF8, "application/geo+json");
        }

        return request;
    }
}
