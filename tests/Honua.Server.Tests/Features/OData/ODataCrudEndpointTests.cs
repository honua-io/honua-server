// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.OData.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Xunit;

namespace Honua.Server.Tests.Features.OData;

[Collection("Database")]
[Protocol(Protocols.ODataV4)]
public sealed class ODataCrudEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private ODataTestFeatureStore _featureStore = null!;
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        _featureStore = new ODataTestFeatureStore();
        _fixture.ReplaceService<ILayerCatalog>(new ODataTestLayerCatalog());
        _fixture.ReplaceService<IFeatureStore>(_featureStore);
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Features({layerId})")]
    public async Task CreateFeature_WithValidPayload_ReturnsCreated()
    {
        var request = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "New City",
                ["population"] = 123456L
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
        created.LayerId.Should().Be(TestLayerId);
        created.Attributes.Should().Contain("New City");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features({layerId},{objectId})")]
    public async Task UpdateFeature_WithValidPayload_ReturnsUpdatedFeature()
    {
        var existing = await _featureStore.CreateAsync(
            TestLayerId,
            Feature.Create(0, null, ImmutableDictionary<string, object?>.Empty.Add("name", "Original")));

        var request = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Updated City"
            }
        };

        var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
        var message = new HttpRequestMessage(new HttpMethod("PATCH"), $"/odata/Features({TestLayerId},{existing.Id})")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var response = await _fixture.Client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        var updated = JsonSerializer.Deserialize(responseContent, ODataJsonContext.Default.ODataFeatureResponse);
        updated.Should().NotBeNull();
        updated!.ObjectId.Should().Be(existing.Id);
        updated.Attributes.Should().Contain("Updated City");
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /odata/Features({layerId},{objectId})")]
    public async Task DeleteFeature_WithValidId_ReturnsNoContent()
    {
        var existing = await _featureStore.CreateAsync(
            TestLayerId,
            Feature.Create(0, null, ImmutableDictionary<string, object?>.Empty.Add("name", "Delete Me")));

        var response = await _fixture.Client.DeleteAsync($"/odata/Features({TestLayerId},{existing.Id})");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
