// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.Create)]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task PublishLayer_SmallintSource_PersistsHintAndPreservesQueriesAfterTypeChange()
    {
        await _fixture.Postgres.ExecuteDdlUnderLockAsync(
            $"ALTER TABLE {_schema}.{_tableName} ALTER COLUMN population TYPE smallint;");
        var published = await PublishLayerAsync(new PublishLayerRequest
        {
            Schema = _schema,
            Table = _tableName,
            LayerName = $"Layer {_tableName}",
            GeometryColumn = "geom",
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            Fields = _idNamePopulationFields,
            ServiceName = _serviceName,
            Enabled = true
        });
        _layerId = published.LayerId;
        var graph = _fixture.GetCurrentV2GraphSnapshot();
        var binding = graph.Graph.StorageBindings.Single(candidate => candidate.StorageLayerId == published.LayerId);
        binding.Options["postgresSmallintColumn:population"].GetBoolean().Should().BeTrue();
        graph.Index.ResourcesById[binding.ResourceId].SchemaFields.Single(field => field.Name == "population")
            .Type.Should().Be(MetadataV2FieldType.Integer);

        await AssertSmallintPublishedQueryAsync("population = 100", expectedCount: 1);
        await AssertSmallintPublishedQueryAsync("population = 40000", expectedCount: 0);
        await AssertSmallintPublishedQueryAsync("population + population = 200", expectedCount: 1);

        // Live source DDL does not require republishing to retain declared integer comparisons.
        await _fixture.Postgres.ExecuteDdlUnderLockAsync($"""
            ALTER TABLE {_schema}.{_tableName} ALTER COLUMN population TYPE numeric;
            UPDATE {_schema}.{_tableName} SET population = 100.4;
            """);
        await AssertSmallintPublishedQueryAsync("population = 100", expectedCount: 1);
        await _fixture.Postgres.ExecuteDdlUnderLockAsync(
            $"ALTER TABLE {_schema}.{_tableName} ALTER COLUMN population TYPE boolean USING population > 0;");
        await AssertSmallintPublishedQueryAsync("population = 1", expectedCount: 1);
    }

    private async Task AssertSmallintPublishedQueryAsync(string filter, int expectedCount)
    {
        using var response = await _client.GetAsync(
            $"/rest/services/{_serviceName}/FeatureServer/{_layerId}/query?f=json&where={Uri.EscapeDataString(filter)}&outFields=id&returnGeometry=false");
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {payload}");
        using var document = JsonDocument.Parse(payload);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse($"response: {payload}");
        document.RootElement.GetProperty("features").GetArrayLength().Should().Be(expectedCount);
    }
}
