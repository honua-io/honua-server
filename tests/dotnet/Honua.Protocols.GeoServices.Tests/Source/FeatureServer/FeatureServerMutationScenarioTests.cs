// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Certification-depth GeoServices protocol mutation scenarios. A new test class instance
/// creates a new database schema, preventing writes from leaking between cases.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class FeatureServerMutationScenarioTests : IAsyncLifetime
{
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.EnableV2ServiceEditingCapabilities(
            TestServiceId,
            ["Create", "Update", "Delete"]);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ApplyEdits, Operations.BulkCreate, Operations.BulkUpdate, Operations.BulkDelete)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/applyEdits")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/addFeatures")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/updateFeatures")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/deleteFeatures")]
    public async Task MutationEndpoints_AcceptValidEdits_AndRoundTripEachState()
    {
        const string serviceApplyPayload = """
            [{"id":0,"adds":[{"attributes":{"name":"mutation-service-apply"},"geometry":{"x":-122.41,"y":37.77}}]}]
            """;
        var serviceApplyResponse = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/applyEdits",
            serviceApplyPayload);
        serviceApplyResponse.Be200Ok();
        using var serviceDocument = JsonDocument.Parse(await serviceApplyResponse.Content.ReadAsStringAsync());
        var serviceAdd = serviceDocument.RootElement.GetProperty("editResults")[0].GetProperty("addResults")[0];
        serviceAdd.GetProperty("success").GetBoolean().Should().BeTrue();
        var serviceObjectId = serviceAdd.GetProperty("objectId").GetInt64();
        await AssertFeatureNameAsync(serviceObjectId, "mutation-service-apply");

        const string layerApplyPayload = """
            {"adds":[{"attributes":{"name":"mutation-layer-apply"},"geometry":{"x":-122.42,"y":37.78}}]}
            """;
        var layerApplyResponse = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits",
            layerApplyPayload);
        var layerApply = await DeserializeEditsAsync(layerApplyResponse);
        layerApply.AddResults.Should().ContainSingle(result => result.Success);
        var layerObjectId = layerApply.AddResults![0].ObjectId!.Value;
        await AssertFeatureNameAsync(layerObjectId, "mutation-layer-apply");

        const string addPayload = """
            {"features":[{"attributes":{"name":"mutation-added"},"geometry":{"x":-122.43,"y":37.79}}]}
            """;
        var addResponse = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/addFeatures",
            addPayload);
        var addResult = await DeserializeEditsAsync(addResponse);
        addResult.AddResults.Should().ContainSingle(result => result.Success);
        var objectId = addResult.AddResults![0].ObjectId!.Value;
        await AssertFeatureNameAsync(objectId, "mutation-added");

        var updatePayload = $$$"""
            {"features":[{"attributes":{"objectid":{{{objectId}}},"name":"mutation-updated"}}],"rollbackOnFailure":true}
            """;
        var updateResponse = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/updateFeatures",
            updatePayload);
        var updateResult = await DeserializeEditsAsync(updateResponse);
        updateResult.UpdateResults.Should().ContainSingle(result => result.Success && result.ObjectId == objectId);
        await AssertFeatureNameAsync(objectId, "mutation-updated");

        var deletePayload = $$$"""{"objectIds":[{{{objectId}}}]}""";
        var deleteResponse = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/deleteFeatures",
            deletePayload);
        var deleteResult = await DeserializeEditsAsync(deleteResponse);
        deleteResult.DeleteResults.Should().ContainSingle(result => result.Success && result.ObjectId == objectId);
        (await ReadFeatureCountAsync(objectId)).Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/applyEdits")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/addFeatures")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/updateFeatures")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/deleteFeatures")]
    public async Task MutationEndpoints_RejectInvalidEdits_WithoutChangingStoredState()
    {
        var originalId = await _fixture.InsertFeatureAsync(TestLayerId, "mutation-original");

        const string malformedServiceApply = """[{"id":0,"adds":[{"attributes":{"name":"invalid-service-apply"}}]""";
        var serviceResponse = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/applyEdits",
            malformedServiceApply);
        await serviceResponse.AssertGeoServicesErrorAsync(400);
        (await ReadCountByNameAsync("invalid-service-apply")).Should().Be(0);

        const string invalidLayerApply = """
            {"adds":[{"attributes":{"name":"invalid-layer-apply"},"geometry":{"rings":[[[-122,37],[-122,38],[-121,38],[-122,37]]]}}]}
            """;
        var layerResponse = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits",
            invalidLayerApply);
        var layerResult = await DeserializeEditsAsync(layerResponse);
        layerResult.AddResults.Should().ContainSingle(result => !result.Success && result.Error != null);
        (await ReadCountByNameAsync("invalid-layer-apply")).Should().Be(0);

        const string invalidAdd = """
            {"features":[{"attributes":{"name":"invalid-add"},"geometry":{"rings":[[[-122,37],[-122,38],[-121,38],[-122,37]]]}}]}
            """;
        var addResponse = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/addFeatures",
            invalidAdd);
        var addResult = await DeserializeEditsAsync(addResponse);
        addResult.AddResults.Should().ContainSingle(result => !result.Success && result.Error != null);
        (await ReadCountByNameAsync("invalid-add")).Should().Be(0);

        const string invalidUpdate = """
            {"features":[{"attributes":{"objectid":999999999,"name":"invalid-update"}}],"rollbackOnFailure":true}
            """;
        var updateResponse = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/updateFeatures",
            invalidUpdate);
        var updateResult = await DeserializeEditsAsync(updateResponse);
        updateResult.UpdateResults.Should().ContainSingle(result => !result.Success && result.Error != null);
        await AssertFeatureNameAsync(originalId, "mutation-original");

        const string invalidDelete = """{"objectIds":"1,,2"}""";
        var deleteResponse = await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/deleteFeatures",
            invalidDelete);
        await deleteResponse.AssertGeoServicesErrorAsync(400);
        await AssertFeatureNameAsync(originalId, "mutation-original");
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string path, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _fixture.Client.PostAsync(path, content);
    }

    private static async Task<ApplyEditsResponse> DeserializeEditsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonSerializer.Deserialize(body, FeatureServerJsonContext.Default.ApplyEditsResponse)
            ?? throw new InvalidOperationException("Expected an apply-edits response.");
    }

    private async Task AssertFeatureNameAsync(long objectId, string expectedName)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=json&objectIds={objectId}&returnGeometry=true");
        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var feature = document.RootElement.GetProperty("features").EnumerateArray().Single();
        feature.GetProperty("attributes").GetProperty("name").GetString().Should().Be(expectedName);
    }

    private async Task<int> ReadFeatureCountAsync(long objectId)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=json&objectIds={objectId}&returnCountOnly=true");
        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("count").GetInt32();
    }

    private async Task<int> ReadCountByNameAsync(string name)
    {
        var where = Uri.EscapeDataString($"name='{name}'");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=json&where={where}&returnCountOnly=true");
        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("count").GetInt32();
    }
}
