// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.OData.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OData;

/// <summary>
/// Integration tests for OData ETag concurrency control:
/// If-Match, If-None-Match, 412 Precondition Failed, 304 Not Modified.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.ODataV4)]
public sealed class ODataETagConcurrencyTests : IAsyncLifetime
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
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task GetFeature_ReturnsETagInResponse()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features(LayerId={TestLayerId},ObjectId=1)");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        document.RootElement.TryGetProperty("@odata.etag", out _).Should().BeTrue(
            "successful GET should include @odata.etag in response body");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task GetFeature_WithMatchingIfNoneMatch_Returns304NotModified()
    {
        // First request to get the ETag
        var firstResponse = await _fixture.Client.GetAsync(
            $"/odata/Features(LayerId={TestLayerId},ObjectId=1)");
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var firstContent = await firstResponse.Content.ReadAsStringAsync();
        using var firstDoc = JsonDocument.Parse(firstContent);
        var etag = firstDoc.RootElement.GetProperty("@odata.etag").GetString();
        etag.Should().NotBeNullOrWhiteSpace();

        // Second request with If-None-Match using the ETag
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/odata/Features(LayerId={TestLayerId},ObjectId=1)");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);

        var secondResponse = await _fixture.Client.SendAsync(request);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task GetFeature_WithNonMatchingIfNoneMatch_Returns200()
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/odata/Features(LayerId={TestLayerId},ObjectId=1)");
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"non-matching-etag\"");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task GetFeature_WithMatchingIfMatch_Returns200()
    {
        // First request to get the ETag
        var firstResponse = await _fixture.Client.GetAsync(
            $"/odata/Features(LayerId={TestLayerId},ObjectId=1)");
        var firstContent = await firstResponse.Content.ReadAsStringAsync();
        using var firstDoc = JsonDocument.Parse(firstContent);
        var etag = firstDoc.RootElement.GetProperty("@odata.etag").GetString();

        // Second request with matching If-Match
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/odata/Features(LayerId={TestLayerId},ObjectId=1)");
        request.Headers.TryAddWithoutValidation("If-Match", etag);

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task GetFeature_WithNonMatchingIfMatch_Returns412PreconditionFailed()
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/odata/Features(LayerId={TestLayerId},ObjectId=1)");
        request.Headers.TryAddWithoutValidation("If-Match", "\"stale-etag-value\"");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task GetFeature_WithIfMatchWildcard_Returns200()
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/odata/Features(LayerId={TestLayerId},ObjectId=1)");
        request.Headers.TryAddWithoutValidation("If-Match", "*");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task UpdateFeature_ETagChangesAfterUpdate()
    {
        var existingId = await _fixture.InsertFeatureAsync(TestLayerId, "ETag Test City");

        // Get original ETag
        var getResponse = await _fixture.Client.GetAsync(
            $"/odata/Features(LayerId={TestLayerId},ObjectId={existingId})");
        var getContent = await getResponse.Content.ReadAsStringAsync();
        using var getDoc = JsonDocument.Parse(getContent);
        var originalEtag = getDoc.RootElement.GetProperty("@odata.etag").GetString();

        // Update the feature
        var updateRequest = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?> { ["name"] = "Updated ETag City" }
        };
        var json = JsonSerializer.Serialize(updateRequest, ODataJsonContext.Default.ODataFeatureRequest);
        var patchMessage = new HttpRequestMessage(new HttpMethod("PATCH"),
            $"/odata/Features(LayerId={TestLayerId},ObjectId={existingId})")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        var patchResponse = await _fixture.Client.SendAsync(patchMessage);
        patchResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var patchContent = await patchResponse.Content.ReadAsStringAsync();
        using var patchDoc = JsonDocument.Parse(patchContent);
        var newEtag = patchDoc.RootElement.GetProperty("@odata.etag").GetString();

        newEtag.Should().NotBe(originalEtag, "ETag should change after update");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task UpdateFeature_WithPreferMinimal_Returns204()
    {
        var existingId = await _fixture.InsertFeatureAsync(TestLayerId, "Prefer Minimal City");

        var updateRequest = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?> { ["name"] = "Minimal Updated" }
        };
        var json = JsonSerializer.Serialize(updateRequest, ODataJsonContext.Default.ODataFeatureRequest);
        var message = new HttpRequestMessage(new HttpMethod("PATCH"),
            $"/odata/Features(LayerId={TestLayerId},ObjectId={existingId})")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        message.Headers.TryAddWithoutValidation("Prefer", "return=minimal");

        var response = await _fixture.Client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.TryGetValues("Preference-Applied", out var preferenceValues).Should().BeTrue();
        preferenceValues.Should().Contain("return=minimal");
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task CreateFeature_WithPreferMinimal_Returns204()
    {
        var request = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?> { ["name"] = "Minimal Created City" }
        };
        var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
        var message = new HttpRequestMessage(HttpMethod.Post,
            $"/odata/Layers({TestLayerId})/Features")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        message.Headers.TryAddWithoutValidation("Prefer", "return=minimal");

        var response = await _fixture.Client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.TryGetValues("Preference-Applied", out var preferenceValues).Should().BeTrue();
        preferenceValues.Should().Contain("return=minimal");
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task CreateFeature_ReturnsLocationAndEntityIdHeaders()
    {
        var request = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?> { ["name"] = "Location Header City" }
        };
        var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
        var response = await _fixture.Client.PostAsync(
            $"/odata/Layers({TestLayerId})/Features",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull("POST 201 should include Location header");
        response.Headers.TryGetValues("OData-EntityId", out var entityIdValues).Should().BeTrue(
            "POST 201 should include OData-EntityId header");
        response.Headers.TryGetValues("EntityId", out var odata401EntityIdValues).Should().BeTrue(
            "OData 4.01 POST 201 should include EntityId header");
        odata401EntityIdValues.Should().Contain(entityIdValues!.Single());
    }
}
