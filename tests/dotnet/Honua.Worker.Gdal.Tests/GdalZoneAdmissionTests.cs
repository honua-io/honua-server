// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Unit coverage for <see cref="GdalZoneAdmission"/> — the zonal-statistics zone
/// count / vertex bound (#2766). Each zone drives its own gdalwarp + gdalinfo
/// subprocess pair, so an unbounded zone FeatureCollection is a cumulative-work
/// DoS; this guard rejects an over-cap payload before the loop runs.
/// </summary>
public sealed class GdalZoneAdmissionTests
{
    private static readonly GeometryFactory Factory = new();

    private static FeatureCollection Points(int count)
    {
        var fc = new FeatureCollection();
        for (var i = 0; i < count; i++)
        {
            fc.Add(new Feature(Factory.CreatePoint(new Coordinate(i, i)), new AttributesTable()));
        }
        return fc;
    }

    private static FeatureCollection SinglePolygon(int vertices)
    {
        // Build a closed ring with `vertices` points (first == last).
        var coords = new Coordinate[vertices];
        for (var i = 0; i < vertices - 1; i++)
        {
            coords[i] = new Coordinate(i % 10, (i * 7) % 10);
        }
        coords[vertices - 1] = coords[0];
        var ring = Factory.CreateLinearRing(coords);
        var polygon = Factory.CreatePolygon(ring);

        var fc = new FeatureCollection();
        fc.Add(new Feature(polygon, new AttributesTable()));
        return fc;
    }

    [UnitTest]
    public void WithinCount_Admits()
    {
        var options = new GdalWorkerOptions { MaxZoneCount = 100 };
        GdalZoneAdmission.TryAdmit(Points(50), options, out _).Should().BeTrue();
    }

    [UnitTest]
    public void OverZoneCount_Rejects()
    {
        var options = new GdalWorkerOptions { MaxZoneCount = 10 };
        GdalZoneAdmission.TryAdmit(Points(11), options, out var error).Should().BeFalse();
        error.Should().Contain("MaxZoneCount");
    }

    [UnitTest]
    public void OverVertexBudget_Rejects()
    {
        // One zone whose ring alone carries more vertices than the cap.
        var options = new GdalWorkerOptions { MaxZoneCount = 10, MaxZoneVertices = 100 };
        GdalZoneAdmission.TryAdmit(SinglePolygon(vertices: 501), options, out var error).Should().BeFalse();
        error.Should().Contain("MaxZoneVertices");
    }

    [UnitTest]
    public void WithinVertexBudget_Admits()
    {
        var options = new GdalWorkerOptions { MaxZoneCount = 10, MaxZoneVertices = 10_000 };
        GdalZoneAdmission.TryAdmit(SinglePolygon(vertices: 100), options, out _).Should().BeTrue();
    }
}
