// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.GeometryService;
using Honua.TestKit.Attributes;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Db.Postgres.Tests.Features.GeometryService;

/// <summary>
/// Proves GeometryServer Web Mercator clamping follows the geographic SRID classifier
/// rather than a literal EPSG:4326 check. The projected Y is the spherical mercator
/// image of the clamped latitude.
/// </summary>
public sealed class PostgresGeometryWebMercatorClampTests
{
    private const double WebMercatorMaxAbsoluteLatitudeDegrees = 85.0511287798066;
    private const double WebMercatorEarthRadiusMeters = 6_378_137d;

    [UnitTest]
    public void ClampSourceForWebMercator_GeographicSouthPole_ClampsToMercatorLimit()
    {
        foreach (var fromSrid in new[] { 4269, 4258, 4283, 4326 })
        {
            var clamped = Read(PostgresGeometryOperationService.ClampSourceForWebMercator(
                Write(new Point(0, -90)),
                fromSrid,
                3857));

            clamped.Y.Should().Be(-WebMercatorMaxAbsoluteLatitudeDegrees);
            var mercatorY = MercatorY(clamped.Y);
            double.IsFinite(mercatorY).Should().BeTrue();
            mercatorY.Should().BeApproximately(-20037508.34, 0.01);
        }
    }

    [UnitTest]
    public void ClampSourceForWebMercator_ProjectedSource_LeavesPlanarOrdinate()
    {
        var original = Write(new Point(0, -90));

        var result = PostgresGeometryOperationService.ClampSourceForWebMercator(original, 32618, 3857);

        Read(result).Y.Should().Be(-90);
    }

    [UnitTest]
    public void ClampSourceForWebMercator_GeographicToGeographic_DoesNotClamp()
    {
        var original = Write(new Point(0, -90));

        var result = PostgresGeometryOperationService.ClampSourceForWebMercator(original, 4269, 4326);

        Read(result).Y.Should().Be(-90);
    }

    private static byte[] Write(Point point) => new WKBWriter().Write(point);

    private static Point Read(byte[] wkb) => (Point)new WKBReader().Read(wkb);

    private static double MercatorY(double latitudeDegrees)
    {
        var phi = latitudeDegrees * Math.PI / 180d;
        return WebMercatorEarthRadiusMeters * Math.Log(Math.Tan((Math.PI / 4d) + (phi / 2d)));
    }
}
