// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.Create)]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers/extents/refresh")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task PublishAndRefreshProjectedLayer_MetadataBoundsUsePublishedCrs()
    {
        var table = $"projected_extent_{Guid.NewGuid():N}";
        try
        {
            await _fixture.Postgres.ExecuteDdlUnderLockAsync($"""
                CREATE TABLE public.{table} (
                    id integer PRIMARY KEY,
                    name text NOT NULL,
                    geom geometry(Point, 3857) NOT NULL);
                INSERT INTO public.{table} VALUES
                    (1, 'first', ST_SetSRID(ST_Point(100, 200), 3857)),
                    (2, 'second', ST_SetSRID(ST_Point(300, 400), 3857));
                """);

            var layer = await PublishLayerAsync(new PublishLayerRequest
            {
                Schema = "public",
                Table = table,
                LayerName = "Projected extent",
                ServiceName = _serviceName,
                GeometryColumn = "geom",
                GeometryType = "Point",
                Srid = 3857,
                PrimaryKey = "id",
                Fields = _idNameFields,
                Enabled = true
            });
            _layerId = layer.LayerId;

            await AssertProjectedMetadataBoundsAsync(100, 200, 300, 400);
            // Catalog caches retain their existing WGS84 contract.
            await AssertPersistedExtentAsync(0.0008983153, 0.0017966306, 0.0026949459, 0.0035932611, 0.0000001);

            await _fixture.Postgres.ExecuteDdlUnderLockAsync($"""
                INSERT INTO public.{table} VALUES
                    (3, 'third', ST_SetSRID(ST_Point(500, 600), 3857));
                """);
            using var refreshed = await _client.PostAsync(
                $"/api/v1/admin/connections/{_connectionId}/layers/extents/refresh?serviceName={_serviceName}",
                content: null);
            refreshed.StatusCode.Should().Be(HttpStatusCode.OK, await refreshed.Content.ReadAsStringAsync());

            await AssertProjectedMetadataBoundsAsync(100, 200, 500, 600);
            await AssertPersistedExtentAsync(0.0008983153, 0.0017966306, 0.0044915764, 0.0053898917, 0.0000001);
        }
        finally
        {
            await _fixture.Postgres.ExecuteDdlUnderLockAsync($"DROP TABLE IF EXISTS public.{table};");
        }
    }

    private async Task AssertProjectedMetadataBoundsAsync(double xmin, double ymin, double xmax, double ymax)
    {
        using var response = await _client.GetAsync(
            $"/rest/services/{_serviceName}/FeatureServer/{_layerId}?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var extent = document.RootElement.GetProperty("extent");
        extent.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(3857);
        extent.GetProperty("xmin").GetDouble().Should().BeApproximately(xmin, 0.0001);
        extent.GetProperty("ymin").GetDouble().Should().BeApproximately(ymin, 0.0001);
        extent.GetProperty("xmax").GetDouble().Should().BeApproximately(xmax, 0.0001);
        extent.GetProperty("ymax").GetDouble().Should().BeApproximately(ymax, 0.0001);
    }
}
