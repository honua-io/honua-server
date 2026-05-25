// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.GeoETL.Domain;
using Honua.Core.Features.GeoETL.Services.Transforms;
using Honua.TestKit.Attributes;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;

namespace Honua.Core.Tests.Features.GeoETL;

/// <summary>
/// Unit coverage for the AGGREGATING (one-to-one summarizing) mode of the
/// managed spatial-join transform — the layer-scope counterpart of ArcGIS
/// SpatialJoin_analysis. For each target feature it summarizes every matched
/// join feature into per-target statistics. All in-memory over NTS feature
/// collections; the join layer is supplied inline as GeoJSON.
/// </summary>
public sealed class SpatialJoinTransformAggregateTests
{
    private static readonly GeometryFactory Factory = new(new PrecisionModel(), 4326);

    // Two join points inside the 0..10 square, one inside the 20..30 square.
    private const string JoinPoints =
        "{\"type\":\"FeatureCollection\",\"features\":[" +
        "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[1,1]},\"properties\":{\"value\":10}}," +
        "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[2,2]},\"properties\":{\"value\":30}}," +
        "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[25,25]},\"properties\":{\"value\":5}}]}";

    [UnitTest]
    public async Task Aggregate_Intersects_CountsAndSumsMatchedJoinFeatures()
    {
        var transform = new SpatialJoinTransform();
        var config = new TransformConfig
        {
            Type = SpatialJoinTransform.TransformType,
            Options = new Dictionary<string, string>
            {
                ["aggregate"] = "true",
                ["predicate"] = "intersects",
                ["referenceInline"] = JoinPoints,
                ["statistics"] = ":count;value:sum;value:mean;value:min;value:max"
            }
        };

        // Two target polygons: one covering both 0..10 points, one covering the lone 20..30 point.
        var targets = Many(
            new Feature(Square(0, 0, 10), Attrs(("zone", "A"))),
            new Feature(Square(20, 20, 10), Attrs(("zone", "B"))));

        var results = await Collect(transform.TransformAsync(config, targets));

        results.Should().HaveCount(2);

        var zoneA = results.Single(f => Equals(f.Attributes!["zone"], "A"));
        Convert.ToInt64(zoneA.Attributes!["JOIN_COUNT"], CultureInfo.InvariantCulture).Should().Be(2);
        Convert.ToDouble(zoneA.Attributes["SUM_value"], CultureInfo.InvariantCulture).Should().Be(40.0);
        Convert.ToDouble(zoneA.Attributes["MEAN_value"], CultureInfo.InvariantCulture).Should().Be(20.0);
        Convert.ToDouble(zoneA.Attributes["MIN_value"], CultureInfo.InvariantCulture).Should().Be(10.0);
        Convert.ToDouble(zoneA.Attributes["MAX_value"], CultureInfo.InvariantCulture).Should().Be(30.0);

        var zoneB = results.Single(f => Equals(f.Attributes!["zone"], "B"));
        Convert.ToInt64(zoneB.Attributes!["JOIN_COUNT"], CultureInfo.InvariantCulture).Should().Be(1);
        Convert.ToDouble(zoneB.Attributes["SUM_value"], CultureInfo.InvariantCulture).Should().Be(5.0);
    }

    [UnitTest]
    public async Task Aggregate_Contains_MatchesPointInPolygon()
    {
        var transform = new SpatialJoinTransform();
        var config = new TransformConfig
        {
            Type = SpatialJoinTransform.TransformType,
            Options = new Dictionary<string, string>
            {
                ["aggregate"] = "true",
                // contains: the join (reference) geometry must contain the target.
                // Use polygon join features and point targets.
                ["predicate"] = "contains",
                ["referenceInline"] =
                    "{\"type\":\"FeatureCollection\",\"features\":[" +
                    "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Polygon\",\"coordinates\":" +
                    "[[[0,0],[10,0],[10,10],[0,10],[0,0]]]},\"properties\":{\"value\":7}}]}",
                ["statistics"] = ":count;value:sum"
            }
        };

        var targets = Many(
            new Feature(Factory.CreatePoint(new Coordinate(5, 5)), Attrs(("id", 1))),
            new Feature(Factory.CreatePoint(new Coordinate(50, 50)), Attrs(("id", 2))));

        var results = await Collect(transform.TransformAsync(config, targets));

        var inside = results.Single(f => Equals(f.Attributes!["id"], 1));
        Convert.ToInt64(inside.Attributes!["JOIN_COUNT"], CultureInfo.InvariantCulture).Should().Be(1);
        Convert.ToDouble(inside.Attributes["SUM_value"], CultureInfo.InvariantCulture).Should().Be(7.0);

        var outside = results.Single(f => Equals(f.Attributes!["id"], 2));
        Convert.ToInt64(outside.Attributes!["JOIN_COUNT"], CultureInfo.InvariantCulture).Should().Be(0);
    }

    [UnitTest]
    public async Task Aggregate_NoMatch_EmitsZeroCountAndPreservesTarget()
    {
        var transform = new SpatialJoinTransform();
        var config = new TransformConfig
        {
            Type = SpatialJoinTransform.TransformType,
            Options = new Dictionary<string, string>
            {
                ["aggregate"] = "true",
                ["referenceInline"] = JoinPoints,
                ["statistics"] = ":count;value:sum"
            }
        };

        var targets = Many(new Feature(Square(100, 100, 1), Attrs(("zone", "empty"))));

        var results = await Collect(transform.TransformAsync(config, targets));

        results.Should().ContainSingle();
        var only = results.Single();
        only.Attributes!["zone"].Should().Be("empty");
        Convert.ToInt64(only.Attributes["JOIN_COUNT"], CultureInfo.InvariantCulture).Should().Be(0);
        only.Attributes["SUM_value"].Should().BeNull();
    }

    [UnitTest]
    public async Task EnrichmentMode_StillTransfersFirstMatch_WhenAggregateFalse()
    {
        // Guard the preserved (default) attribute-transfer behavior.
        var transform = new SpatialJoinTransform();
        var config = new TransformConfig
        {
            Type = SpatialJoinTransform.TransformType,
            Options = new Dictionary<string, string>
            {
                ["referenceInline"] =
                    "{\"type\":\"FeatureCollection\",\"features\":[" +
                    "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Polygon\",\"coordinates\":" +
                    "[[[0,0],[10,0],[10,10],[0,10],[0,0]]]},\"properties\":{\"region\":\"west\"}}]}",
                ["predicate"] = "contains",
                ["transfer"] = "region"
            }
        };

        var results = await Collect(transform.TransformAsync(
            config,
            Many(new Feature(Factory.CreatePoint(new Coordinate(5, 5)), Attrs(("id", 1))))));

        results.Should().ContainSingle();
        results.Single().Attributes!["region"].Should().Be("west");
    }

    [UnitTest]
    public async Task Aggregate_UnknownStatistic_Throws()
    {
        var transform = new SpatialJoinTransform();
        var config = new TransformConfig
        {
            Type = SpatialJoinTransform.TransformType,
            Options = new Dictionary<string, string>
            {
                ["aggregate"] = "true",
                ["referenceInline"] = JoinPoints,
                ["statistics"] = "value:stddev"
            }
        };

        var act = async () => await Collect(transform.TransformAsync(
            config,
            Many(new Feature(Square(0, 0, 10), new AttributesTable()))));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [UnitTest]
    public async Task UnknownPredicate_Throws()
    {
        var transform = new SpatialJoinTransform();
        var config = new TransformConfig
        {
            Type = SpatialJoinTransform.TransformType,
            Options = new Dictionary<string, string>
            {
                ["aggregate"] = "true",
                ["referenceInline"] = JoinPoints,
                ["predicate"] = "touches"
            }
        };

        var act = async () => await Collect(transform.TransformAsync(
            config,
            Many(new Feature(Square(0, 0, 10), new AttributesTable()))));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static Polygon Square(double x, double y, double size) =>
        Factory.CreatePolygon(
        [
            new Coordinate(x, y),
            new Coordinate(x + size, y),
            new Coordinate(x + size, y + size),
            new Coordinate(x, y + size),
            new Coordinate(x, y),
        ]);

    private static AttributesTable Attrs(params (string Name, object? Value)[] values)
    {
        var table = new AttributesTable();
        foreach (var (name, value) in values)
        {
            table.Add(name, value);
        }

        return table;
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
