// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Formats;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// The zoom-0 tile of a dense layer used to be refused with 413. Every vector-tile route that
/// shares the PostGIS encoder now returns a decodable tile inside <c>Limits:Tiles:MaxTileSize</c>.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
[Protocol(TestProtocols.OgcApiTiles)]
[Protocol(TestProtocols.VectorTileServer)]
[Operation(Operations.GetTile)]
[Collection("Database")]
public sealed class DenseLowZoomMvtBudgetEndpointTests : IAsyncLifetime
{
    private const int Budget = 32_768;
    private const int LayerId = 0;

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureServices(services => services.PostConfigure<LimitsOptions>(options =>
            options.Tiles.MaxTileSize = Budget));

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf")]
    public async Task GetTile_DenseZoom0_ReturnsDecodableTileWithinBudget()
    {
        await SeedDenseLayerAsync();

        string[] urls =
        [
            "/ogc/tiles/collections/0/tiles/WebMercatorQuad/0/0/0",
            "/ogc/tiles/collections/0/tiles/WorldCRS84Quad/0/0/0",
            $"/tiles/{LayerId}/0/0/0.mvt",
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/tile/0/0/0.pbf",
        ];

        foreach (var url in urls)
        {
            using var response = await _fixture.Client.GetAsync(url);
            var body = await response.Content.ReadAsByteArrayAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, url + " " + Describe(body));
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.mapbox-vector-tile", url);
            body.Length.Should().BeGreaterThan(0, url).And.BeLessThanOrEqualTo(Budget, url);

            var tile = MvtTileDecoder.Decode(body);
            tile.FeatureCount.Should().BeGreaterThan(0, url);
            tile.Layer("layer").Features.Should().Contain(
                feature => feature.GeometryType == MvtGeometryType.Polygon, url);
            foreach (var feature in tile.Layer("layer").Features)
            {
                switch (feature.GeometryType)
                {
                    case MvtGeometryType.Point:
                        feature.Points.Should().NotBeEmpty(url);
                        break;
                    case MvtGeometryType.LineString:
                        feature.Points.Count().Should().BeGreaterOrEqualTo(2, url);
                        break;
                    case MvtGeometryType.Polygon:
                        foreach (var ring in feature.Rings)
                        {
                            ring.Count.Should().BeGreaterOrEqualTo(4, url);
                            ring[0].Should().Be(ring[^1], url);
                            TwiceArea(ring).Should().NotBe(0, url);
                        }

                        break;
                }
            }
        }
    }

    private async Task SeedDenseLayerAsync()
    {
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO features (layer_id, geometry, attributes)
            SELECT @layerId,
                   ST_SetSRID(ST_MakePoint(-120 + (g * 0.02), 35), 4326),
                   jsonb_build_object('name', 'dense-' || g::text, 'payload', repeat('x', 4096) || g::text)
            FROM generate_series(1, 16) AS g;
            INSERT INTO features (layer_id, geometry, attributes) VALUES
                (@layerId, ST_SetSRID(ST_GeomFromText('POLYGON((-140 20, -135 20, -135 25, -140 25, -140 20))'), 4326),
                    jsonb_build_object('name', 'dense-box-a', 'payload', repeat('y', 4096))),
                (@layerId, ST_SetSRID(ST_GeomFromText('POLYGON((-80 10, -75 10, -75 15, -80 15, -80 10))'), 4326),
                    jsonb_build_object('name', 'dense-box-b', 'payload', repeat('z', 4096)));
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        (await command.ExecuteNonQueryAsync()).Should().Be(18);
    }

    private static string Describe(byte[] body)
    {
        if (body.Length == 0)
        {
            return "(empty body)";
        }

        if (body[0] is (byte)'{' or (byte)'<')
        {
            return Encoding.UTF8.GetString(body);
        }

        return $"{body.Length} bytes";
    }

    private static long TwiceArea(IReadOnlyList<MvtPoint> ring)
    {
        long area = 0;
        for (var i = 0; i < ring.Count - 1; i++)
        {
            area += (ring[i].X * ring[i + 1].Y) - (ring[i + 1].X * ring[i].Y);
        }

        return area;
    }
}
