// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Db.Postgres.Features.FileImport;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Db.Postgres.UnitTests.Features.Import;

/// <summary>
/// A projected import aimed at a geographic target is cut after the ordinates are
/// degrees. The vertex and ring limits are applied to that cut, not only to the source.
/// </summary>
public sealed class AntimeridianImportCutTests
{
    [UnitTest]
    public void CreateWkb_WebMercatorCrossingPolygon_StoresTheShortSideInTheTargetSrid()
    {
        var service = CreateService(new ImportLimits());
        var feature = new Feature(WebMercatorCrossing(), new AttributesTable());

        var result = InvokeCreateWkb(service, feature, targetSrid: 4326);

        result.Skipped.Should().BeFalse();
        feature.Geometry!.SRID.Should().Be(4326);
        var stored = new WKBReader().Read(result.Wkb!);
        stored.Should().BeOfType<MultiPolygon>();
        stored.Area.Should().BeApproximately(200d, 1d);
        MaxLongitudeSpan(stored).Should().BeLessThanOrEqualTo(20d);
    }

    [UnitTest]
    public void CreateWkb_CutThatExceedsTheRingLimit_IsRejected()
    {
        var service = CreateService(new ImportLimits { MaxRings = 1, SkipInvalidGeometry = true });
        var feature = new Feature(WebMercatorCrossing(), new AttributesTable());

        var result = InvokeCreateWkb(service, feature, targetSrid: 4326);

        result.Skipped.Should().BeTrue();
        result.Wkb.Should().BeNull();
    }

    private static StreamingFileImportService CreateService(ImportLimits limits)
        => new(
            new ThrowingConnectionProvider(),
            new NoopCrsDetectionService(),
            new TestFileFormatDetectionService(),
            new NoopPerformanceMonitor(),
            NullLogger<StreamingFileImportService>.Instance,
            limits: limits);

    private static (byte[]? Wkb, bool Skipped) InvokeCreateWkb(
        StreamingFileImportService service,
        IFeature feature,
        int targetSrid)
    {
        var tallyType = typeof(StreamingFileImportService).GetNestedType(
            "GeometryRepairTally",
            BindingFlags.NonPublic);
        var tally = Activator.CreateInstance(tallyType!, nonPublic: true);
        var method = typeof(StreamingFileImportService).GetMethod(
            "CreateWkb",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var raw = method!.Invoke(service, [feature, new WKBWriter(), tally!, targetSrid]);
        var type = raw!.GetType();
        return (
            (byte[]?)type.GetProperty("Wkb")!.GetValue(raw),
            (bool)type.GetProperty("Skipped")!.GetValue(raw)!);
    }

    private static Polygon WebMercatorCrossing()
    {
        var ring = new LinearRing(
        [
            Mercator(170, 0),
            Mercator(-170, 0),
            Mercator(-170, 10),
            Mercator(170, 10),
            Mercator(170, 0)
        ]);
        return new Polygon(ring) { SRID = 3857 };
    }

    private static Coordinate Mercator(double longitude, double latitude)
    {
        var (x, y) = WebMercatorMath.LonLatToWebMercator(longitude, latitude);
        return new Coordinate(x, y);
    }

    private static double MaxLongitudeSpan(Geometry geometry)
    {
        var max = 0d;
        for (var i = 0; i < geometry.NumGeometries; i++)
        {
            if (geometry.GetGeometryN(i) is not Polygon polygon)
            {
                continue;
            }

            for (var point = 1; point < polygon.ExteriorRing.NumPoints; point++)
            {
                max = Math.Max(
                    max,
                    Math.Abs(polygon.ExteriorRing.GetCoordinateN(point).X
                        - polygon.ExteriorRing.GetCoordinateN(point - 1).X));
            }
        }

        return max;
    }
}
