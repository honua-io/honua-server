// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Formats;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Decodes the vector tiles the MVT endpoint actually returns and asserts their contents
/// (honua-server#4421). Nothing in the repository decoded an MVT before
/// <see cref="MvtTileDecoder"/>: every existing assertion checked a content-type header, or
/// <c>NotBeEmpty()</c>, and most were wrapped in <c>BeOneOf(OK, NoContent)</c> plus an <c>if</c>
/// that made them vacuous on the empty-tile failure mode they nominally guarded. A tile pipeline
/// that clipped wrongly, ignored <c>where=</c>, dropped features at low zoom, or emitted an
/// undecodable payload passed all of them.
/// </summary>
/// <remarks>
/// Every expected tile coordinate below is computed from the Web Mercator definition and the
/// tile-matrix arithmetic in <see cref="TileGeometry"/> — not read back out of a previous run — so
/// these are oracles, not snapshots. The tolerance is +/-2 tile units on a 4096-unit tile
/// (0.05%), which absorbs the producer's integer rounding without admitting a wrong coordinate.
/// </remarks>
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.GetTile)]
[Collection("Database")]
public sealed class MvtTileDecodeTests : IAsyncLifetime
{
    private const int LayerId = 0;
    private const int Extent = 4096;

    /// <summary>The default <c>TileOptions.TileBuffer</c>, in tile units.</summary>
    private const int Buffer = 256;

    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_SeededPoint_DecodesToOneFeatureAtTheExpectedTileCoordinate()
    {
        const double lon = -122.4194;
        const double lat = 37.7749;
        const int zoom = 12;
        await SeedAsync("mvt-point", $"POINT({lon.ToString(CultureInfo.InvariantCulture)} {lat.ToString(CultureInfo.InvariantCulture)})");

        var (x, y) = TileGeometry.TileOf(lon, lat, zoom);
        // The seeded fixture already holds features on this layer, so scope to the row this test
        // inserted; the where clause itself is proven by GetTile_WithWhereClause_* below.
        var tile = await DecodeTileAsync(zoom, x, y, "where=" + Uri.EscapeDataString("name='mvt-point'"));

        var layer = tile.Layer("layer");
        layer.Version.Should().Be(2, "the producer must emit vector tile spec 2.x");
        layer.Extent.Should().Be(Extent);
        var feature = layer.Features.Should().ContainSingle().Subject;
        feature.GeometryType.Should().Be(MvtGeometryType.Point);
        feature.Attributes.Should().ContainKey("name").WhoseValue.Should().Be("mvt-point");

        var expected = TileGeometry.ToTileSpace(lon, lat, zoom, x, y, Extent);
        var actual = feature.Points.Should().ContainSingle().Subject;
        ((double)actual.X).Should().BeApproximately(expected.X, 2d, "the encoded X must be the point's position in tile space");
        ((double)actual.Y).Should().BeApproximately(expected.Y, 2d, "the encoded Y must be the point's position in tile space");
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_WithWhereClause_ChangesTheDecodedFeatureSet()
    {
        // The existing coverage for this parameter is a one-line body asserting
        // BeOneOf(OK, NoContent) — nothing verified that where= changed the tile at all.
        const double lon = -122.4194;
        const double lat = 37.7749;
        const int zoom = 12;
        await SeedAsync("mvt-keep", $"POINT({F(lon)} {F(lat)})");
        await SeedAsync("mvt-drop", $"POINT({F(lon + 0.001)} {F(lat + 0.001)})");

        var (x, y) = TileGeometry.TileOf(lon, lat, zoom);

        var unfiltered = await DecodeTileAsync(
            zoom, x, y, "where=" + Uri.EscapeDataString("name LIKE 'mvt-%'"));
        NamesOf(unfiltered).Should().BeEquivalentTo(["mvt-keep", "mvt-drop"]);

        var filtered = await DecodeTileAsync(zoom, x, y, "where=" + Uri.EscapeDataString("name='mvt-keep'"));
        NamesOf(filtered).Should().BeEquivalentTo(
            ["mvt-keep"],
            "the where clause must remove the non-matching feature from the encoded tile, not merely " +
            "return a 200");
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_GeometryCrossingTheTileBoundary_IsClippedToTheBufferedExtent()
    {
        // Production clips with ST_AsMVTGeom against the tile envelope with a 256-unit buffer.
        // No test asserted that a geometry crossing a tile boundary is clipped: every test that
        // configured tiling set TileBuffer = 0, which disables the behaviour being claimed.
        const int zoom = 12;
        const double lon = -122.4194;
        const double lat = 37.7749;
        var (x, y) = TileGeometry.TileOf(lon, lat, zoom);
        var bounds = TileGeometry.BoundsDegrees(x, y, zoom);

        // A line running well outside the tile on both sides, through its middle.
        var midLat = (bounds.MinLat + bounds.MaxLat) / 2;
        await SeedAsync(
            "mvt-crossing",
            $"LINESTRING({F(bounds.MinLon - 1)} {F(midLat)}, {F(bounds.MaxLon + 1)} {F(midLat)})");
        // And a feature far outside the buffered envelope, which must not appear at all.
        await SeedAsync("mvt-elsewhere", $"POINT({F(bounds.MinLon - 5)} {F(midLat)})");

        var tile = await DecodeTileAsync(
            zoom, x, y, "where=" + Uri.EscapeDataString("name LIKE 'mvt-%'"));

        NamesOf(tile).Should().BeEquivalentTo(
            ["mvt-crossing"],
            "a feature outside the tile's buffered envelope must be filtered out entirely");

        var clipped = tile.Layer("layer").Features.Single();
        clipped.GeometryType.Should().Be(MvtGeometryType.LineString);
        var points = clipped.Points.ToArray();
        points.Should().HaveCountGreaterThanOrEqualTo(2);
        points.Should().OnlyContain(
            point => point.X >= -Buffer - 1 && point.X <= Extent + Buffer + 1,
            $"every clipped ordinate must lie within the tile extent plus its {Buffer}-unit buffer");
        points.Should().OnlyContain(point => point.Y >= -Buffer - 1 && point.Y <= Extent + Buffer + 1);

        // The source line spans roughly 3 tiles; the clipped one must be cut back to the buffered
        // tile, so its span cannot exceed the buffered width.
        var span = points.Max(point => point.X) - points.Min(point => point.X);
        span.Should().BeLessThanOrEqualTo(
            Extent + (2 * Buffer) + 2,
            "the line must be cut at the buffer, not carried through at full length");
        span.Should().BeGreaterThan(
            Extent - 2,
            "the line crosses the whole tile, so the clipped result must still span it");
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_AtLowZoom_SimplifiesTheGeometryWithoutDroppingTheFeature()
    {
        // TileOptions.SimplifyZoom defaults to 10 and TileMath.GetSimplificationTolerance(8) is
        // 500 m. Only the scalar tolerance lookup was tested; nothing asserted that simplification
        // actually reduces vertices, or that it preserves the feature and its endpoints.
        const double lon = -122.4194;
        const double lat = 37.7749;
        const int sourceVertices = 400;
        var wkt = TileGeometry.DenseZigZagWkt(lon, lat, sourceVertices);
        await SeedAsync("mvt-dense", wkt);

        var filter = "where=" + Uri.EscapeDataString("name='mvt-dense'");
        var (detailedX, detailedY) = TileGeometry.TileOf(lon, lat, 14);
        var detailed = await DecodeTileAsync(14, detailedX, detailedY, filter);
        var detailedPoints = detailed.Layer("layer").Features.Should().ContainSingle().Subject.Points.Count();

        var (coarseX, coarseY) = TileGeometry.TileOf(lon, lat, 8);
        var coarse = await DecodeTileAsync(8, coarseX, coarseY, filter);
        var coarseFeature = coarse.Layer("layer").Features.Should().ContainSingle(
            "simplification must not drop the feature").Subject;
        var coarsePoints = coarseFeature.Points.ToArray();

        coarsePoints.Length.Should().BeLessThan(
            detailedPoints,
            "z=8 is at or below SimplifyZoom, so ST_SimplifyPreserveTopology must reduce the vertex count");
        coarsePoints.Length.Should().BeGreaterThanOrEqualTo(
            2, "a simplified line must remain a line");
        coarseFeature.Attributes.Should().ContainKey("name").WhoseValue.Should().Be(
            "mvt-dense", "simplification must not disturb attributes");
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_WhenTheTileHasNoFeatures_ReturnsNoContentRatherThanAnUndecodablePayload()
    {
        // The existing tests admitted NoContent through BeOneOf and then skipped every assertion,
        // so an always-empty pipeline satisfied them. Pin the two outcomes separately: empty means
        // 204 with no body, and non-empty means a payload that decodes.
        await SeedAsync("mvt-somewhere", "POINT(-122.4194 37.7749)");
        var (emptyX, emptyY) = TileGeometry.TileOf(20.0, -30.0, 12);

        using var empty = await _fixture.Client.GetAsync($"/tiles/{LayerId}/12/{emptyX}/{emptyY}.mvt");

        empty.StatusCode.Should().Be(
            HttpStatusCode.NoContent,
            "a tile with no features is 204 — not a 200 carrying bytes that do not decode");
        (await empty.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string[] NamesOf(MvtTile tile)
        => [.. tile.Layer("layer").Features
            .Select(feature => feature.Attributes.TryGetValue("name", out var name) ? name as string : null)
            .Where(name => name is not null)
            .Select(name => name!)];

    private async Task<MvtTile> DecodeTileAsync(int z, int x, int y, string? query = null)
    {
        var url = $"/tiles/{LayerId}/{z}/{x}/{y}.mvt" + (query is null ? string.Empty : "?" + query);
        using var response = await _fixture.Client.GetAsync(url);
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.mapbox-vector-tile");
        var payload = await response.Content.ReadAsByteArrayAsync();
        payload.Should().NotBeEmpty();
        return MvtTileDecoder.Decode(payload);
    }

    private async Task SeedAsync(string name, string wkt)
    {
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO features (layer_id, geometry, attributes)
            VALUES (@layerId, ST_SetSRID(ST_GeomFromText(@wkt), 4326), jsonb_build_object('name', @name));
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        command.Parameters.AddWithValue("wkt", wkt);
        command.Parameters.AddWithValue("name", name);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    /// <summary>
    /// The Web Mercator / XYZ tile arithmetic, implemented here independently of the server so the
    /// expected values are an oracle rather than a snapshot of the producer's own output.
    /// </summary>
    internal static class TileGeometry
    {
        private const double EarthRadius = 6378137d;
        private const double HalfWorld = 20037508.342789244d;

        public static (int X, int Y) TileOf(double lon, double lat, int zoom)
        {
            var n = 1 << zoom;
            var x = (int)Math.Floor((lon + 180d) / 360d * n);
            var latRad = lat * Math.PI / 180d;
            var y = (int)Math.Floor((1d - (Math.Log(Math.Tan(latRad) + (1d / Math.Cos(latRad))) / Math.PI)) / 2d * n);
            return (Math.Clamp(x, 0, n - 1), Math.Clamp(y, 0, n - 1));
        }

        public static (double MinLon, double MinLat, double MaxLon, double MaxLat) BoundsDegrees(
            int x, int y, int zoom)
        {
            var n = 1 << zoom;
            var minLon = (x / (double)n * 360d) - 180d;
            var maxLon = ((x + 1) / (double)n * 360d) - 180d;
            var maxLat = LatitudeOf(y, n);
            var minLat = LatitudeOf(y + 1, n);
            return (minLon, minLat, maxLon, maxLat);
        }

        public static (double X, double Y) ToTileSpace(
            double lon, double lat, int zoom, int tileX, int tileY, int extent)
        {
            var n = 1 << zoom;
            var tileSize = HalfWorld * 2d / n;
            var originX = -HalfWorld + (tileX * tileSize);
            var originY = HalfWorld - (tileY * tileSize);
            var mercatorX = EarthRadius * lon * Math.PI / 180d;
            var mercatorY = EarthRadius * Math.Log(Math.Tan((Math.PI / 4d) + (lat * Math.PI / 360d)));
            return ((mercatorX - originX) / tileSize * extent, (originY - mercatorY) / tileSize * extent);
        }

        /// <summary>
        /// A dense zig-zag line centred on the supplied point, with alternating sub-metre-scale
        /// excursions that survive at high zoom and collapse under a 500 m tolerance.
        /// </summary>
        public static string DenseZigZagWkt(double lon, double lat, int vertices)
        {
            var points = new List<string>(vertices);
            for (var i = 0; i < vertices; i++)
            {
                var offset = i * 0.00002d;
                var wobble = (i % 2 == 0 ? 1 : -1) * 0.000015d;
                points.Add(
                    $"{F(lon + offset)} {F(lat + wobble)}");
            }

            return $"LINESTRING({string.Join(", ", points)})";
        }

        private static double LatitudeOf(int y, int n)
        {
            var value = Math.PI * (1d - (2d * y / n));
            return 180d / Math.PI * Math.Atan(Math.Sinh(value));
        }
    }
}
