// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests;

public sealed partial class QueryRelatedRecordsEndpointTests
{
    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithGlobalIds_WhenLayerHasNoGlobalIdField_Returns400()
    {
        var response = await GetWithRetryAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords?globalIds=11111111-1111-1111-1111-111111111111&relationshipId={TestRelationshipId}");

        await response.AssertGeoServicesErrorAsync(400);
        (await response.Content.ReadAsStringAsync()).Should().Contain("globalIds is not supported for this layer");
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithInvalidGlobalIds_Returns400()
    {
        var response = await GetWithRetryAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords?globalIds=not-a-uuid&relationshipId={TestRelationshipId}");

        await response.AssertGeoServicesErrorAsync(400);
        (await response.Content.ReadAsStringAsync()).Should().Contain("globalIds must contain valid UUID values");
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task QueryRelatedRecords_WithGlobalIds_ReturnsMatchingRelatedRecordGroups()
    {
        await UseMutableFixtureAsync();
        const string featureGlobalId = "55555555-5555-5555-5555-555555555555";
        await SeedGlobalIdOnFirstFeatureAsync("GlobalID", featureGlobalId);

        var braced = Uri.EscapeDataString("{" + featureGlobalId.ToUpperInvariant() + "}");
        var matched = await QueryRelatedByGlobalIdsAsync(braced);
        matched.RelatedRecordGroups.Should().Contain(group => group.ObjectId == 1);

        var unmatched = await QueryRelatedByGlobalIdsAsync("66666666-6666-6666-6666-666666666666");
        unmatched.RelatedRecordGroups.Should().BeEmpty();
    }

    private async Task SeedGlobalIdOnFirstFeatureAsync(string globalIdField, string featureGlobalId)
    {
        _mutableFixture!.UpdateV2ResourceSchemaField(TestLayerId, new MetadataV2Field
        {
            Name = globalIdField,
            Type = MetadataV2FieldType.Uuid,
            Nullable = true,
            Description = "Global ID"
        });
        _mutableFixture.UpdateV2ResourceEditing(TestLayerId, new MetadataV2ObjectEditing
        {
            GlobalIdField = globalIdField
        });

        await using var connection = await _mutableFixture.Postgres.GetConnectionAsync(_mutableFixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE features
            SET attributes = jsonb_set(COALESCE(attributes, '{}'::jsonb), '{GlobalID}', to_jsonb(@gid::text), true)
            WHERE layer_id = 0 AND objectid = 1;
            """;
        command.Parameters.AddWithValue("gid", featureGlobalId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<QueryRelatedRecordsResponse> QueryRelatedByGlobalIdsAsync(string globalIds)
    {
        var response = await GetWithRetryAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryRelatedRecords?globalIds={globalIds}&relationshipId={TestRelationshipId}");
        response.Be200Ok();
        return JsonSerializer.Deserialize<QueryRelatedRecordsResponse>(
            await response.Content.ReadAsStringAsync(),
            FeatureServerJsonContext.Default.QueryRelatedRecordsResponse)!;
    }
}
