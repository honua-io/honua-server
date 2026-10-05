// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit;
using Microsoft.Extensions.ObjectPool;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

/// <summary>
/// Membership oracles for the WithinDistance envelope prefilter (honua-server#5461). Every
/// expected match is proven to lie inside the geodesic radius by an independent ellipsoidal
/// path-length upper bound (a parallel or meridian arc on WGS84; the geodesic is never longer),
/// so a missing row means the index prefilter dropped a true match before the exact
/// <c>ST_DWithin(geography)</c> check could see it.
/// </summary>
[Collection("Database")]
public sealed class FeatureQueryBuilderDistancePrefilterIntegrationTests(PostgresFixture fixture)
{
    private const double SemiMajorAxis = 6378137.0;
    private const double Flattening = 1 / 298.257223563;
    private static readonly double EccentricitySquared = Flattening * (2 - Flattening);

    // Web Mercator northing of latitude 60 degrees.
    private const double MercatorNorthingAt60 = 8399737.889818355;

    [Fact]
    public async Task WebMercatorAtLatitude60_KeepsMatchOutsideTheMetreEnvelope()
    {
        // 1000 Web Mercator metres at latitude 60 are ~501 ground metres: inside a 750 m radius,
        // but outside an envelope expanded by 750 projected units.
        var latitude = InverseWebMercatorLatitude(MercatorNorthingAt60);
        ParallelArcUpperBound(latitude, 1000 / SemiMajorAxis).Should().BeLessThan(750);
        ParallelArcUpperBound(latitude, 3000 / SemiMajorAxis).Should().BeGreaterThan(1500);

        var ids = await QueryWithinDistanceAsync(
            srid: 3857,
            features: [(1, 1000, MercatorNorthingAt60), (2, 3000, MercatorNorthingAt60)],
            filter: new Point(0, MercatorNorthingAt60),
            meters: 750);

        ids.Should().Equal(1);
    }

    [Fact]
    public async Task WebMercatorAcrossTheAntimeridian_KeepsMatchOnTheOtherSide()
    {
        // 180 degrees east is x = pi * a. The query sits 508 m west of it, the feature 108 m east
        // of -180: 617 m apart along the equator.
        var halfWorld = Math.PI * SemiMajorAxis;
        var query = new Point(halfWorld - 508.34, 0);
        var feature = (1L, -halfWorld + 108.34, 0.0);
        ParallelArcUpperBound(0, (508.34 + 108.34) / SemiMajorAxis).Should().BeLessThan(1000);

        var ids = await QueryWithinDistanceAsync(3857, [feature, (2, 0, 0)], query, meters: 1000);

        ids.Should().Equal(1);
    }

    [Fact]
    public async Task GeographicAtTheEquator_KeepsMatchBeyondTheSphericalDegreeEnvelope()
    {
        // 1000 / 111320 = 0.0089831 degrees, but 0.009 degrees of latitude at the equator is only
        // ~995 m on the ellipsoid.
        MeridianArcUpperBound(0, 0.009).Should().BeLessThan(1000);

        var ids = await QueryWithinDistanceAsync(
            4326, [(1, 0, 0.009), (2, 0, 0.02)], new Point(0, 0), meters: 1000);

        ids.Should().Equal(1);
    }

    [Theory]
    [InlineData(89.999)]
    [InlineData(-89.999)]
    public async Task GeographicNearAPole_KeepsMatchAtADistantLongitude(double latitude)
    {
        // A quarter turn of longitude 0.001 degrees from the pole is ~175 m along the parallel.
        ParallelArcUpperBound(latitude, Math.PI / 2).Should().BeLessThan(1000);

        var ids = await QueryWithinDistanceAsync(
            4326, [(1, 90, latitude), (2, 0, latitude > 0 ? 89.9 : -89.9)], new Point(0, latitude), meters: 1000);

        ids.Should().Equal(1);
    }

    [Fact]
    public async Task GeographicLineFilter_KeepsMatchUnderTheGeodesicBulge()
    {
        // The geodesic between (-10, 60) and (10, 60) peaks near latitude 60.377 at longitude 0
        // (spherical vertex atan(tan 60 / cos 10)). A point at (0, 60.36) is within ~2 km of it,
        // far inside 5 km, yet ~30 km north of the line's degree envelope.
        var vertexLatitude = Math.Atan(Math.Tan(Radians(60)) / Math.Cos(Radians(10))) * 180 / Math.PI;
        MeridianArcUpperBound(60.36, vertexLatitude).Should().BeLessThan(2500);

        var line = new LineString([new Coordinate(-10, 60), new Coordinate(10, 60)]);
        var ids = await QueryWithinDistanceAsync(4326, [(1, 0, 60.36), (2, 0, 60.6)], line, meters: 5000);

        ids.Should().Equal(1);
    }

    [Fact]
    public async Task ProjectedFootLayer_ConvertsTheMetreRadiusToNativeFeet()
    {
        // EPSG:2264 (NAD83 / North Carolina, US survey feet): 50 ftUS = 15.24 m, 100 ftUS = 30.48 m.
        var ids = await QueryWithinDistanceAsync(
            2264, [(1, 2000050, 700000), (2, 2000100, 700000)], new Point(2000000, 700000), meters: 20);

        ids.Should().Equal(1);
    }

    private async Task<long[]> QueryWithinDistanceAsync(
        int srid,
        (long Id, double X, double Y)[] features,
        Geometry filter,
        double meters)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("DistancePrefilter");
        try
        {
            var rows = string.Join(
                ",\n",
                features.Select(feature => string.Create(
                    CultureInfo.InvariantCulture,
                    $"({feature.Id}, 1, ST_SetSRID(ST_MakePoint({feature.X:R}, {feature.Y:R}), {srid}))")));
            await fixture.ExecuteAsync($"""
                CREATE TABLE {schema}.features (
                    objectid bigint PRIMARY KEY,
                    layer_id integer NOT NULL,
                    geometry geometry(Point, {srid}));
                CREATE INDEX ON {schema}.features USING gist (geometry);
                INSERT INTO {schema}.features VALUES {rows};
                ANALYZE {schema}.features;
                """);

            var pool = new DefaultObjectPoolProvider().Create(
                new Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy());
            var builder = new FeatureQueryBuilder(pool, new GeometryProcessor(), schema);
            var query = builder.BuildObjectIdsQuery(1, new FeatureQuery
            {
                SpatialReferenceSrid = srid,
                SpatialFilter = SpatialFilter.CreateDistanceFilter(
                    new WKBWriter().Write(filter),
                    distance: meters,
                    unit: DistanceUnit.Meters,
                    withinDistance: true,
                    srid: srid)
            });

            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = query.Sql;
            command.Parameters.AddWithValue(1);
            foreach (var parameter in query.WhereParameters)
            {
                command.Parameters.AddWithValue(parameter);
            }

            var ids = new List<long>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetInt64(0));
            }

            ids.Sort();
            return [.. ids];
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;

    private static double InverseWebMercatorLatitude(double northing)
        => (2 * Math.Atan(Math.Exp(northing / SemiMajorAxis)) - Math.PI / 2) * 180 / Math.PI;

    private static double PrimeVerticalRadius(double latitude)
        => SemiMajorAxis / Math.Sqrt(1 - EccentricitySquared * Math.Pow(Math.Sin(Radians(latitude)), 2));

    // Arc length along the parallel at a fixed geodetic latitude: N(phi) * cos(phi) * dLambda.
    private static double ParallelArcUpperBound(double latitude, double longitudeRadians)
        => PrimeVerticalRadius(latitude) * Math.Cos(Radians(latitude)) * longitudeRadians;

    // Meridian arc length bounded by the largest meridional radius M(phi) over the interval,
    // reached at the latitude of greatest magnitude.
    private static double MeridianArcUpperBound(double fromLatitude, double toLatitude)
    {
        var maxAbsLatitude = Math.Max(Math.Abs(fromLatitude), Math.Abs(toLatitude));
        var sin = Math.Sin(Radians(maxAbsLatitude));
        var meridionalRadius = SemiMajorAxis * (1 - EccentricitySquared) / Math.Pow(1 - EccentricitySquared * sin * sin, 1.5);
        return meridionalRadius * Radians(Math.Abs(toLatitude - fromLatitude));
    }
}
