// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed partial class FeatureServerMutationScenarioTests
{
    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithUseGlobalIds_AddsUpdatesAndDeletesByGlobalId()
    {
        ConfigureGlobalIdField("GlobalID");
        await AssertSupportsApplyEditsWithGlobalIdsAsync();

        const string suppliedGuid = "22222222-2222-2222-2222-222222222222";
        var (clientOid, serverOid, serverGid) = await AddGlobalIdPairAsync(suppliedGuid);
        await UpdateGlobalIdPairAndAssertAsync((clientOid, suppliedGuid), (serverOid, serverGid));
        await DeleteGlobalIdPairAndAssertAsync((clientOid, suppliedGuid), (serverOid, serverGid));
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/applyEdits")]
    public async Task ServiceApplyEdits_WithUseGlobalIds_MutatesByGlobalIdAndReturnsBothIds()
    {
        ConfigureGlobalIdField("GlobalID");
        const string suppliedGuid = "33333333-3333-3333-3333-333333333333";

        var addPayload = $$$"""
            [{"id":0,"adds":[{"attributes":{"name":"svc-gid-add","GlobalID":"{{{suppliedGuid}}}"},"geometry":{"x":-122.41,"y":37.77}}]}]
            """;
        var objectId = await AssertServiceEditSuccessAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/applyEdits?useGlobalIds=true", addPayload, ("addResults", suppliedGuid));

        var updatePayload = $$$"""
            [{"id":0,"useGlobalIds":true,"updates":[{"attributes":{"GlobalID":"{{{suppliedGuid}}}","name":"svc-gid-updated"}}]}]
            """;
        (await AssertServiceEditSuccessAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/applyEdits", updatePayload, ("updateResults", suppliedGuid)))
            .Should().Be(objectId);
        await AssertFeatureNameAsync(objectId, "svc-gid-updated");

        var deletePayload = $$$"""
            [{"id":0,"deletes":["{{{suppliedGuid}}}"]}]
            """;
        (await AssertServiceEditSuccessAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/applyEdits?useGlobalIds=true", deletePayload, ("deleteResults", suppliedGuid)))
            .Should().Be(objectId);
        (await ReadFeatureCountAsync(objectId)).Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithUseGlobalIds_RejectsMissingAndInvalidGlobalIds()
    {
        ConfigureGlobalIdField("GlobalID");
        const string missingGuid = "44444444-4444-4444-4444-444444444444";
        var payload = $$$"""
            {
              "useGlobalIds": true,
              "adds": [{"attributes":{"name":"bad-gid","GlobalID":"not-a-uuid"},"geometry":{"x":-122.41,"y":37.77}}],
              "updates": [
                {"attributes":{"name":"missing-gid-attr"}},
                {"attributes":{"GlobalID":"{{{missingGuid}}}","name":"missing-row"}}
              ],
              "deletes": ["not-a-uuid", "{{{missingGuid}}}"]
            }
            """;
        var result = await DeserializeEditsAsync(await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", payload));

        result.Success.Should().BeFalse();
        result.AddResults![0].Success.Should().BeFalse();
        result.UpdateResults![0].Success.Should().BeFalse();
        result.UpdateResults[1].Success.Should().BeFalse();
        result.UpdateResults[1].GlobalId.Should().Be(missingGuid);
        result.DeleteResults![0].Success.Should().BeFalse();
        result.DeleteResults[1].Success.Should().BeFalse();
        result.DeleteResults[1].GlobalId.Should().Be(missingGuid);
    }

    private async Task AssertSupportsApplyEditsWithGlobalIdsAsync()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}?f=json");
        response.Be200Ok();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("supportsApplyEditsWithGlobalIds").GetBoolean().Should().BeTrue();
    }

    private async Task<(long ClientOid, long ServerOid, string ServerGid)> AddGlobalIdPairAsync(string suppliedGuid)
    {
        var payload = $$$"""
            {
              "useGlobalIds": true,
              "adds": [
                {"attributes":{"name":"gid-add-client","GlobalID":"{{{suppliedGuid.ToUpperInvariant()}}}"},"geometry":{"x":-122.41,"y":37.77}},
                {"attributes":{"name":"gid-add-server"},"geometry":{"x":-122.42,"y":37.78}}
              ]
            }
            """;
        var result = await DeserializeEditsAsync(await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", payload));
        result.Success.Should().BeTrue();
        result.AddResults.Should().HaveCount(2);
        result.AddResults![0].GlobalId.Should().Be(suppliedGuid);
        Guid.TryParse(result.AddResults[1].GlobalId, out _).Should().BeTrue();
        return (result.AddResults[0].ObjectId!.Value, result.AddResults[1].ObjectId!.Value, result.AddResults[1].GlobalId!);
    }

    private async Task UpdateGlobalIdPairAndAssertAsync((long Oid, string Gid) first, (long Oid, string Gid) second)
    {
        var payload = $$$"""
            {
              "useGlobalIds": true,
              "updates": [
                {"attributes":{"GlobalID":"{{{first.Gid.ToUpperInvariant()}}}","name":"gid-updated-client"}},
                {"attributes":{"GlobalID":"{{{second.Gid}}}","name":"gid-updated-server"}}
              ]
            }
            """;
        var result = await DeserializeEditsAsync(await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", payload));
        result.Success.Should().BeTrue();
        result.UpdateResults![0].Should().Match<EditResult>(r => r.Success && r.ObjectId == first.Oid && r.GlobalId == first.Gid);
        result.UpdateResults[1].Should().Match<EditResult>(r => r.Success && r.ObjectId == second.Oid && r.GlobalId == second.Gid);
        await AssertFeatureNameAsync(first.Oid, "gid-updated-client");
        await AssertFeatureNameAsync(second.Oid, "gid-updated-server");
    }

    private async Task DeleteGlobalIdPairAndAssertAsync((long Oid, string Gid) first, (long Oid, string Gid) second)
    {
        var payload = $$$"""
            {"useGlobalIds":true,"deletes":["{{{first.Gid.ToUpperInvariant()}}}","{{{second.Gid}}}"]}
            """;
        var result = await DeserializeEditsAsync(await PostJsonAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", payload));
        result.Success.Should().BeTrue();
        result.DeleteResults![0].Should().Match<EditResult>(r => r.Success && r.ObjectId == first.Oid && r.GlobalId == first.Gid);
        result.DeleteResults[1].Should().Match<EditResult>(r => r.Success && r.ObjectId == second.Oid && r.GlobalId == second.Gid);
        (await ReadFeatureCountAsync(first.Oid)).Should().Be(0);
        (await ReadFeatureCountAsync(second.Oid)).Should().Be(0);
    }

    private async Task<long> AssertServiceEditSuccessAsync(
        string path,
        string payload,
        (string ResultsKey, string ExpectedGuid) expected)
    {
        var response = await PostJsonAsync(path, payload);
        response.Be200Ok();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = doc.RootElement[0].GetProperty(expected.ResultsKey)[0];
        entry.GetProperty("success").GetBoolean().Should().BeTrue();
        entry.GetProperty("globalId").GetString().Should().Be(expected.ExpectedGuid);
        return entry.GetProperty("objectId").GetInt64();
    }

    private void ConfigureGlobalIdField(string fieldName)
    {
        _fixture.UpdateV2ResourceSchemaField(TestLayerId, new MetadataV2Field
        {
            Name = fieldName,
            Type = MetadataV2FieldType.Uuid,
            Nullable = true,
            Description = "Global ID",
        });
        _fixture.UpdateV2ResourceEditing(TestLayerId, new MetadataV2ObjectEditing
        {
            GlobalIdField = fieldName,
        });
    }
}
