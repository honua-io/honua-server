// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.GeoETL.Domain;
using Honua.Core.Features.GeoETL.Services.Transforms;
using Honua.TestKit.Attributes;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;

namespace Honua.Core.Tests.Features.GeoETL;

/// <summary>
/// Unit coverage for the managed (NetTopologySuite, GEOS-free) per-feature
/// geometry-operation transform that lifts the single-geometry geoprocessing
/// operators to layer scope. All run in-memory over streamed feature collections.
/// </summary>
public sealed class GeometryOperationTransformTests
{
    private static readonly GeometryFactory Factory = new(new PrecisionModel(), 4326);

    [UnitTest]
    public async Task Buffer_ExpandsEachGeometryAndCarriesAttributes()
    {
        var transform = new GeometryOperationTransform();
        var config = new TransformConfig
        {
            Type = GeometryOperationTransform.TransformType,
            Options = new Dictionary<string, string> { ["op"] = "buffer", ["distance"] = "1" }
        };
        var feature = new Feature(
            Factory.CreatePoint(new Coordinate(0, 0)),
            new AttributesTable { { "name", "origin" } });

        var results = await Collect(transform.TransformAsync(config, Many(feature)));

        results.Should().ContainSingle();
        var buffered = results.Single();
        buffered.Geometry!.Area.Should().BeApproximately(Math.PI, 0.05);
        buffered.Geometry.SRID.Should().Be(4326);
        buffered.Attributes!["name"].Should().Be("origin");
    }

    [UnitTest]
    public async Task Simplify_CollapsesBelowToleranceVertices()
    {
        var transform = new GeometryOperationTransform();
        var config = new TransformConfig
        {
            Type = GeometryOperationTransform.TransformType,
            Options = new Dictionary<string, string> { ["op"] = "simplify", ["tolerance"] = "1.0" }
        };
        var feature = new Feature(
            Factory.CreateLineString([new Coordinate(0, 0), new Coordinate(5, 0.001), new Coordinate(10, 0)]),
            new AttributesTable());

        var results = await Collect(transform.TransformAsync(config, Many(feature)));

        results.Should().ContainSingle();
        ((LineString)results.Single().Geometry!).NumPoints.Should().Be(2);
    }

    [UnitTest]
    public async Task ConvexHull_WrapsEachFeature()
    {
        var transform = new GeometryOperationTransform();
        var config = new TransformConfig
        {
            Type = GeometryOperationTransform.TransformType,
            Options = new Dictionary<string, string> { ["op"] = "convex-hull" }
        };
        var multipoint = Factory.CreateMultiPoint(
        [
            Factory.CreatePoint(new Coordinate(0, 0)),
            Factory.CreatePoint(new Coordinate(10, 0)),
            Factory.CreatePoint(new Coordinate(10, 10)),
            Factory.CreatePoint(new Coordinate(0, 10)),
            Factory.CreatePoint(new Coordinate(5, 5)),
        ]);
        var feature = new Feature(multipoint, new AttributesTable());

        var results = await Collect(transform.TransformAsync(config, Many(feature)));

        results.Should().ContainSingle();
        results.Single().Geometry!.Area.Should().BeApproximately(100.0, 1e-6);
    }

    [UnitTest]
    public async Task Centroid_ReducesEachFeatureToItsCenterPoint()
    {
        var transform = new GeometryOperationTransform();
        var config = new TransformConfig
        {
            Type = GeometryOperationTransform.TransformType,
            Options = new Dictionary<string, string> { ["op"] = "centroid" }
        };
        var box = Factory.CreatePolygon(
        [
            new Coordinate(0, 0), new Coordinate(10, 0), new Coordinate(10, 10),
            new Coordinate(0, 10), new Coordinate(0, 0),
        ]);
        var feature = new Feature(box, new AttributesTable { { "id", 7L } });

        var results = await Collect(transform.TransformAsync(config, Many(feature)));

        results.Should().ContainSingle();
        var point = (Point)results.Single().Geometry!;
        point.X.Should().BeApproximately(5.0, 1e-9);
        point.Y.Should().BeApproximately(5.0, 1e-9);
        results.Single().Attributes!["id"].Should().Be(7L);
    }

    [UnitTest]
    public async Task MakeValid_RepairsSelfIntersectingPolygon()
    {
        var transform = new GeometryOperationTransform();
        var config = new TransformConfig
        {
            Type = GeometryOperationTransform.TransformType,
            Options = new Dictionary<string, string> { ["op"] = "make-valid" }
        };
        // Bow-tie polygon (self-intersecting) — invalid as input.
        var bowtie = Factory.CreatePolygon(
        [
            new Coordinate(0, 0), new Coordinate(10, 10), new Coordinate(10, 0),
            new Coordinate(0, 10), new Coordinate(0, 0),
        ]);
        bowtie.IsValid.Should().BeFalse();
        var feature = new Feature(bowtie, new AttributesTable());

        var results = await Collect(transform.TransformAsync(config, Many(feature)));

        results.Should().ContainSingle();
        results.Single().Geometry!.IsValid.Should().BeTrue();
    }

    [UnitTest]
    public async Task Difference_SubtractsRegionFromEachFeature()
    {
        var transform = new GeometryOperationTransform();
        var config = new TransformConfig
        {
            Type = GeometryOperationTransform.TransformType,
            Options = new Dictionary<string, string>
            {
                ["op"] = "difference",
                ["regionWkt"] = "POLYGON((5 0, 15 0, 15 10, 5 10, 5 0))"
            }
        };
        var box = Factory.CreatePolygon(
        [
            new Coordinate(0, 0), new Coordinate(10, 0), new Coordinate(10, 10),
            new Coordinate(0, 10), new Coordinate(0, 0),
        ]);
        var feature = new Feature(box, new AttributesTable());

        var results = await Collect(transform.TransformAsync(config, Many(feature)));

        results.Should().ContainSingle();
        // 10x10 box minus its right half (5..10) leaves the left 5x10 = area 50.
        results.Single().Geometry!.Area.Should().BeApproximately(50.0, 1e-6);
    }

    [UnitTest]
    public async Task Buffer_DropsNullGeometryFeatures()
    {
        var transform = new GeometryOperationTransform();
        var config = new TransformConfig
        {
            Type = GeometryOperationTransform.TransformType,
            Options = new Dictionary<string, string> { ["op"] = "buffer", ["distance"] = "1" }
        };
        var withGeometry = new Feature(Factory.CreatePoint(new Coordinate(0, 0)), new AttributesTable());
        var empty = new Feature(Factory.CreatePoint(), new AttributesTable());

        var results = await Collect(transform.TransformAsync(config, Many(withGeometry, empty)));

        results.Should().ContainSingle();
    }

    [UnitTest]
    public async Task UnknownOp_Throws()
    {
        var transform = new GeometryOperationTransform();
        var config = new TransformConfig
        {
            Type = GeometryOperationTransform.TransformType,
            Options = new Dictionary<string, string> { ["op"] = "voronoi" }
        };

        var act = async () => await Collect(transform.TransformAsync(
            config,
            Many(new Feature(Factory.CreatePoint(new Coordinate(0, 0)), new AttributesTable()))));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static async Task<List<IFeature>> Collect(IAsyncEnumerable<IFeature> source)
    {
        var list = new List<IFeature>();
        await foreach (var feature in source)
        {
            list.Add(feature);
        }

        return list;
    }

    private static async IAsyncEnumerable<IFeature> Many(params IFeature[] features)
    {
        foreach (var feature in features)
        {
            yield return feature;
        }

        await Task.CompletedTask;
    }
}
