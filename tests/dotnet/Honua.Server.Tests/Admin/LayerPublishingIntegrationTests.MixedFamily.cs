// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.Create)]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task PublishMixedGeometry_ChosenFamily_ServesOnlyThatFamily()
    {
        var table = $"mixed_family_{Guid.NewGuid():N}";
        try
        {
            await _fixture.Postgres.ExecuteDdlUnderLockAsync($"""
                CREATE TABLE public.{table} (
                    id integer PRIMARY KEY,
                    name text NOT NULL,
                    geom geometry);
                INSERT INTO public.{table} (id, name, geom) VALUES
                    (1, 'point', ST_SetSRID(ST_Point(1, 2), 4326)),
                    (2, 'line', ST_SetSRID(ST_MakeLine(ST_Point(0, 0), ST_Point(3, 4)), 4326)),
                    (3, 'empty', NULL);
                """);

            var points = await PublishLayerAsync(new PublishLayerRequest
            {
                Schema = "public",
                Table = table,
                LayerName = "Mixed points",
                ServiceName = _serviceName,
                GeometryColumn = "geom",
                GeometryType = "Point",
                Srid = 4326,
                PrimaryKey = "id",
                Fields = _idNameFields,
                Enabled = true
            });
            _layerId = points.StorageLayerId;
            points.GeometryType.Should().Be("Point");
            using (var pointItems = JsonDocument.Parse(await ReadOgcItemsAsync("limit=10")))
            {
                var features = pointItems.RootElement.GetProperty("features");
                features.GetArrayLength().Should().Be(1);
                features[0].GetProperty("geometry").GetProperty("type").GetString().Should().Be("Point");
                features[0].GetProperty("properties").GetProperty("name").GetString().Should().Be("point");
            }

            var lines = await PublishLayerAsync(new PublishLayerRequest
            {
                Schema = "public",
                Table = table,
                LayerName = "Mixed lines",
                ServiceName = _serviceName,
                GeometryColumn = "geom",
                GeometryType = "LineString",
                Srid = 4326,
                PrimaryKey = "id",
                Fields = _idNameFields,
                Enabled = true
            });
            _layerId = lines.StorageLayerId;
            lines.GeometryType.Should().Be("LineString");
            using var lineItems = JsonDocument.Parse(await ReadOgcItemsAsync("limit=10"));
            var lineFeatures = lineItems.RootElement.GetProperty("features");
            lineFeatures.GetArrayLength().Should().Be(1);
            lineFeatures[0].GetProperty("geometry").GetProperty("type").GetString().Should().Be("LineString");
            lineFeatures[0].GetProperty("properties").GetProperty("name").GetString().Should().Be("line");
        }
        finally
        {
            await _fixture.Postgres.ExecuteDdlUnderLockAsync($"""
                DROP VIEW IF EXISTS public.{table}__honua_pt;
                DROP VIEW IF EXISTS public.{table}__honua_ln;
                DROP VIEW IF EXISTS public.{table}__honua_pg;
                DROP TABLE IF EXISTS public.{table};
                """);
        }
    }
}
