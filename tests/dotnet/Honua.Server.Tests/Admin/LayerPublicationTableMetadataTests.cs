// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishRetainedTable_AdvertisesTableAndQueriesRowsWithoutSpatialMetadata()
    {
        await using (var connection = await _fixture.Postgres.GetConnectionAsync())
        await using (var command = new NpgsqlCommand($"ALTER TABLE public.{_tableName} DROP COLUMN geom", connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        var request = new PublishLayerRequest
        {
            Schema = _schema, Table = _tableName, LayerName = _tableName,
            PrimaryKey = "id", ServiceName = _serviceName
        };
        using var response = await _client.PostAsync($"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(request, options: _jsonOptions));
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, payload);
        _layerId = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary>>(payload, _jsonOptions)!.Data!.LayerId;

        var serviceUrl = $"/rest/services/{_serviceName}/FeatureServer";
        using var serviceResponse = await _client.GetAsync(serviceUrl + "?f=json");
        serviceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var service = JsonDocument.Parse(await serviceResponse.Content.ReadAsStringAsync());
        service.RootElement.GetProperty("layers").GetArrayLength().Should().Be(0);
        service.RootElement.GetProperty("tables").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("id").GetInt32().Should().Be(_layerId);

        using var metadataResponse = await _client.GetAsync($"{serviceUrl}/{_layerId}?f=json");
        metadataResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync());
        metadata.RootElement.GetProperty("type").GetString().Should().Be("Table");
        metadata.RootElement.TryGetProperty("geometryType", out _).Should().BeFalse();
        metadata.RootElement.TryGetProperty("spatialReference", out _).Should().BeFalse();
        metadata.RootElement.GetProperty("supportsReturningQueryExtent").GetBoolean().Should().BeFalse();

        using var queryResponse = await _client.GetAsync($"{serviceUrl}/{_layerId}/query?f=json&where=1%3D1&outFields=*&returnGeometry=false");
        queryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var query = JsonDocument.Parse(await queryResponse.Content.ReadAsStringAsync());
        query.RootElement.GetProperty("features").GetArrayLength().Should().BeGreaterThan(0);
        foreach (var row in query.RootElement.GetProperty("features").EnumerateArray())
        {
            row.GetProperty("attributes").TryGetProperty("name", out _).Should().BeTrue();
        }
    }
}
