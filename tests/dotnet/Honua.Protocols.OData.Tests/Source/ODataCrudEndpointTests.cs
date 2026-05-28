// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Protocols.OData.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.OData;

[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataCrudEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
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
            $"/odata/Layers({TestLayerId})/Features",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);
        var created = document.RootElement;
        created.GetProperty("ObjectId").GetInt64().Should().BeGreaterThan(0);
        created.GetProperty("LayerId").GetInt32().Should().Be(TestLayerId);
        var createdAttributes = ODataTestHelpers.ParseAttributes(created);
        createdAttributes.GetProperty("name").GetString().Should().Be("New City");
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Features")]
    public async Task CreateFeature_WithLayerIdInPayload_ReturnsCreated()
    {
        var payload = new Dictionary<string, object?>
        {
            ["LayerId"] = TestLayerId,
            ["Attributes"] = new Dictionary<string, object?>
            {
                ["name"] = "New City"
            }
        };

        var json = JsonSerializer.Serialize(payload, ODataJsonContext.Default.DictionaryStringObject);
        var response = await _fixture.Client.PostAsync(
            "/odata/Features",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);
        var created = document.RootElement;
        created.GetProperty("ObjectId").GetInt64().Should().BeGreaterThan(0);
        created.GetProperty("LayerId").GetInt32().Should().Be(TestLayerId);
        var createdAttributes = ODataTestHelpers.ParseAttributes(created);
        createdAttributes.GetProperty("name").GetString().Should().Be("New City");
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task CreateFeature_WithUnsupportedContentType_ReturnsUnsupportedMediaType()
    {
        var response = await _fixture.Client.PostAsync(
            $"/odata/Layers({TestLayerId})/Features",
            new StringContent("""{"Attributes":{"name":"Bad Type"}}""", Encoding.UTF8, "text/plain"));

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features({layerId},{objectId})")]
    public async Task UpdateFeature_WithValidPayload_ReturnsUpdatedFeature()
    {
        var existingId = await _fixture.InsertFeatureAsync(TestLayerId, "Original");

        var request = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Updated City"
            }
        };

        var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
        var message = new HttpRequestMessage(new HttpMethod("PATCH"), $"/odata/Features({TestLayerId},{existingId})")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var response = await _fixture.Client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);
        var updated = document.RootElement;
        updated.GetProperty("ObjectId").GetInt64().Should().Be(existingId);
        var updatedAttributes = ODataTestHelpers.ParseAttributes(updated);
        updatedAttributes.GetProperty("name").GetString().Should().Be("Updated City");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features({layerId},{objectId})")]
    public async Task UpdateFeature_WithUnsupportedContentType_ReturnsUnsupportedMediaType()
    {
        var existingId = await _fixture.InsertFeatureAsync(TestLayerId, "Original");
        using var message = new HttpRequestMessage(
            new HttpMethod("PATCH"),
            $"/odata/Features({TestLayerId},{existingId})")
        {
            Content = new StringContent("""{"Attributes":{"name":"Bad Type"}}""", Encoding.UTF8, "text/plain")
        };

        var response = await _fixture.Client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /odata/Layers({layerId})/Features({objectId})")]
    public async Task ReplaceFeature_WithUnsupportedContentType_ReturnsUnsupportedMediaType()
    {
        var existingId = await _fixture.InsertFeatureAsync(TestLayerId, "Original");
        using var message = new HttpRequestMessage(
            HttpMethod.Put,
            $"/odata/Layers({TestLayerId})/Features({existingId})")
        {
            Content = new StringContent("""{"Attributes":{"name":"Bad Type"}}""", Encoding.UTF8, "text/plain")
        };

        var response = await _fixture.Client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task UpdateFeature_WithNamedKeys_ReturnsUpdatedFeature()
    {
        var existingId = await _fixture.InsertFeatureAsync(TestLayerId, "Original");

        var request = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Updated City"
            }
        };

        var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
        var message = new HttpRequestMessage(new HttpMethod("PATCH"), $"/odata/Features(LayerId={TestLayerId},ObjectId={existingId})")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var response = await _fixture.Client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);
        var updated = document.RootElement;
        updated.GetProperty("ObjectId").GetInt64().Should().Be(existingId);
        var updatedAttributes = ODataTestHelpers.ParseAttributes(updated);
        updatedAttributes.GetProperty("name").GetString().Should().Be("Updated City");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Layers({layerId})/Features({objectId})")]
    public async Task UpdateFeature_WithLayerPath_ReturnsUpdatedFeature()
    {
        var existingId = await _fixture.InsertFeatureAsync(TestLayerId, "Original");

        var request = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Updated City"
            }
        };

        var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
        var message = new HttpRequestMessage(new HttpMethod("PATCH"), $"/odata/Layers({TestLayerId})/Features({existingId})")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var response = await _fixture.Client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);
        var updated = document.RootElement;
        updated.GetProperty("ObjectId").GetInt64().Should().Be(existingId);
        var updatedAttributes = ODataTestHelpers.ParseAttributes(updated);
        updatedAttributes.GetProperty("name").GetString().Should().Be("Updated City");
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task DeleteFeature_WithValidId_ReturnsNoContent()
    {
        var existingId = await _fixture.InsertFeatureAsync(TestLayerId, "Delete Me");

        var response = await _fixture.Client.DeleteAsync(
            $"/odata/Features(LayerId={TestLayerId},ObjectId={existingId})");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

}
