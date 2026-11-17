// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    // The Console layer preview frames its camera on this listed extent.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /api/v1/admin/connections/{id}/layers")]
    public async Task ListLayers_ReturnsStoredWgs84Extent()
    {
        var table = $"listed_extent_{Guid.NewGuid():N}";
        try
        {
            await PublishTwoPointLayerAsync(table);

            using var response = await _client.GetAsync(
                $"/api/v1/admin/connections/{_connectionId}/layers?serviceName={_serviceName}");
            response.Be200Ok();
            var payload = await response.Content.ReadAsStringAsync();
            AdminListContractAssertions.MatchesPublishedSchema("/connections/{id}/layers", payload);

            using var document = JsonDocument.Parse(payload);
            var listed = document.RootElement.GetProperty("data").GetProperty("items").EnumerateArray()
                .Single(item => item.GetProperty("storageLayerId").GetInt32() == _layerId);
            var extent = listed.GetProperty("extent");
            extent.GetProperty("minX").GetDouble().Should().BeApproximately(-74.006, 0.000001);
            extent.GetProperty("minY").GetDouble().Should().BeApproximately(40.7128, 0.000001);
            extent.GetProperty("maxX").GetDouble().Should().BeApproximately(-73.9465, 0.000001);
            extent.GetProperty("maxY").GetDouble().Should().BeApproximately(40.8116, 0.000001);
        }
        finally
        {
            await _fixture.Postgres.ExecuteDdlUnderLockAsync($"DROP TABLE IF EXISTS public.{table};");
        }
    }

    private async Task PublishTwoPointLayerAsync(string table)
    {
        await _fixture.Postgres.ExecuteDdlUnderLockAsync($"""
            CREATE TABLE public.{table} (
                id integer PRIMARY KEY,
                name text NOT NULL,
                geom geometry(Point, 4326) NOT NULL);
            INSERT INTO public.{table} VALUES
                (1, 'south-west', ST_SetSRID(ST_Point(-74.006, 40.7128), 4326)),
                (2, 'north-east', ST_SetSRID(ST_Point(-73.9465, 40.8116), 4326));
            """);
        var layer = await PublishLayerAsync(new PublishLayerRequest
        {
            Schema = "public",
            Table = table,
            LayerName = "Listed extent",
            ServiceName = _serviceName,
            GeometryColumn = "geom",
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            Fields = _idNameFields,
            Enabled = true
        });
        _layerId = layer.StorageLayerId;
    }
}
