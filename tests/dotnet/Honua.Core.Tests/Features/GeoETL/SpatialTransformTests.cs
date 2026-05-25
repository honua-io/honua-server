// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.GeoETL.Domain;
using Honua.Core.Features.GeoETL.Services.Transforms;
using Honua.TestKit.Attributes;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;

namespace Honua.Core.Tests.Features.GeoETL;

/// <summary>
/// Unit coverage for the managed (NetTopologySuite, GEOS-free) spatial transforms:
/// spatial filter, clip, spatial-join enrichment, and dedup. All run in-memory.
/// </summary>
public sealed class SpatialTransformTests
{
    private static readonly GeometryFactory Factory = new(new PrecisionModel(), 4326);

    [UnitTest]
    public async Task SpatialFilter_BboxKeepsOnlyFeaturesInside()
    {
        var transform = new SpatialFilterTransform();
        var config = new TransformConfig
        {
            Type = SpatialFilterTransform.TransformType,
            Options = new Dictionary<string, string> { ["bbox"] = "0,0,10,10" }
        };
        var inside = new Feature(Factory.CreatePoint(new Coordinate(5, 5)), new AttributesTable());
        var outside = new Feature(Factory.CreatePoint(new Coordinate(20, 20)), new AttributesTable());

        var results = await Collect(transform.TransformAsync(config, Many(inside, outside)));

        results.Should().HaveCount(1);
        ((Point)results.Single().Geometry!).X.Should().Be(5);
    }

    [UnitTest]
    public async Task Clip_TrimsGeometryToRegionAndDropsOutside()
    {
        var transform = new ClipTransform();
        var config = new TransformConfig
        {
            Type = ClipTransform.TransformType,
            Options = new Dictionary<string, string> { ["bbox"] = "0,0,10,10" }
        };

        // A line crossing the clip boundary should be trimmed to the region.
        var crossing = new Feature(
            Factory.CreateLineString([new Coordinate(-5, 5), new Coordinate(15, 5)]),
            new AttributesTable { { "id", 1L } });
        var outside = new Feature(
            Factory.CreatePoint(new Coordinate(50, 50)),
            new AttributesTable { { "id", 2L } });

        var results = await Collect(transform.TransformAsync(config, Many(crossing, outside)));

        results.Should().HaveCount(1);
        var clipped = results.Single().Geometry!;
        clipped.SRID.Should().Be(4326);
        clipped.EnvelopeInternal.MinX.Should().BeApproximately(0, 0.0001);
        clipped.EnvelopeInternal.MaxX.Should().BeApproximately(10, 0.0001);
    }

    [UnitTest]
    public async Task SpatialJoin_TransfersReferenceAttributesForPointInPolygon()
    {
        const string reference = """
            {
              "type": "FeatureCollection",
              "features": [
                { "type": "Feature",
                  "geometry": { "type": "Polygon",
                    "coordinates": [[[0,0],[0,10],[10,10],[10,0],[0,0]]] },
                  "properties": { "zone": "A", "pop": 100 } }
              ]
            }
            """;
        var transform = new SpatialJoinTransform();
        var config = new TransformConfig
        {
            Type = SpatialJoinTransform.TransformType,
            Options = new Dictionary<string, string>
            {
                ["referenceInline"] = reference,
                ["predicate"] = "contains",
                ["transfer"] = "zone",
                ["prefix"] = "ref_"
            }
        };
        var inZone = new Feature(Factory.CreatePoint(new Coordinate(5, 5)),
            new AttributesTable { { "name", "p1" } });
        var outOfZone = new Feature(Factory.CreatePoint(new Coordinate(50, 50)),
            new AttributesTable { { "name", "p2" } });

        var results = await Collect(transform.TransformAsync(config, Many(inZone, outOfZone)));

        results.Should().HaveCount(2);
        var enriched = results.Single(f =>
            string.Equals(f.Attributes!.GetOptionalValue("name")?.ToString(), "p1", StringComparison.Ordinal));
        enriched.Attributes!.GetOptionalValue("ref_zone").Should().Be("A");

        var unmatched = results.Single(f =>
            string.Equals(f.Attributes!.GetOptionalValue("name")?.ToString(), "p2", StringComparison.Ordinal));
        unmatched.Attributes!.Exists("ref_zone").Should().BeFalse();
    }

    [UnitTest]
    public async Task SpatialJoin_InnerJoinDropsUnmatched()
    {
        const string reference = """
            {
              "type": "FeatureCollection",
              "features": [
                { "type": "Feature",
                  "geometry": { "type": "Polygon",
                    "coordinates": [[[0,0],[0,10],[10,10],[10,0],[0,0]]] },
                  "properties": { "zone": "A" } }
              ]
            }
            """;
        var transform = new SpatialJoinTransform();
        var config = new TransformConfig
        {
            Type = SpatialJoinTransform.TransformType,
            Options = new Dictionary<string, string>
            {
                ["referenceInline"] = reference,
                ["keepUnmatched"] = "false"
            }
        };
        var inZone = new Feature(Factory.CreatePoint(new Coordinate(5, 5)), new AttributesTable());
        var outOfZone = new Feature(Factory.CreatePoint(new Coordinate(50, 50)), new AttributesTable());

        var results = await Collect(transform.TransformAsync(config, Many(inZone, outOfZone)));

        results.Should().HaveCount(1);
        results.Single().Attributes!.GetOptionalValue("zone").Should().Be("A");
    }

    [UnitTest]
    public async Task Dedup_ByAttributeKey_KeepsFirstOnly()
    {
        var transform = new DedupTransform();
        var config = new TransformConfig
        {
            Type = DedupTransform.TransformType,
            Options = new Dictionary<string, string> { ["keys"] = "id" }
        };
        var a1 = new Feature(Factory.CreatePoint(new Coordinate(0, 0)),
            new AttributesTable { { "id", "x" }, { "seq", 1L } });
        var a2 = new Feature(Factory.CreatePoint(new Coordinate(1, 1)),
            new AttributesTable { { "id", "x" }, { "seq", 2L } });
        var b1 = new Feature(Factory.CreatePoint(new Coordinate(2, 2)),
            new AttributesTable { { "id", "y" }, { "seq", 3L } });

        var results = await Collect(transform.TransformAsync(config, Many(a1, a2, b1)));

        results.Should().HaveCount(2);
        results.Select(f => f.Attributes!.GetOptionalValue("seq")).Should().BeEquivalentTo([1L, 3L]);
    }

    [UnitTest]
    public async Task Dedup_ByGeometry_KeepsDistinctGeometriesOnly()
    {
        var transform = new DedupTransform();
        var config = new TransformConfig
        {
            Type = DedupTransform.TransformType,
            Options = new Dictionary<string, string> { ["geometry"] = "true" }
        };
        var p1 = new Feature(Factory.CreatePoint(new Coordinate(1, 1)), new AttributesTable());
        var p1Dup = new Feature(Factory.CreatePoint(new Coordinate(1, 1)), new AttributesTable());
        var p2 = new Feature(Factory.CreatePoint(new Coordinate(2, 2)), new AttributesTable());

        var results = await Collect(transform.TransformAsync(config, Many(p1, p1Dup, p2)));

        results.Should().HaveCount(2);
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
