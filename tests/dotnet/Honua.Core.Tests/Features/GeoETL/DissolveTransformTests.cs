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
/// Unit coverage for the managed (NetTopologySuite, GEOS-free) group-aware
/// dissolve transform — the layer-scope counterpart of ArcGIS
/// Dissolve_management. All run in-memory over streamed feature collections.
/// </summary>
public sealed class DissolveTransformTests
{
    private static readonly GeometryFactory Factory = new(new PrecisionModel(), 4326);

    [UnitTest]
    public async Task Dissolve_GroupsByField_UnionsGeometriesPerGroup()
    {
        var transform = new DissolveTransform();
        var config = new TransformConfig
        {
            Type = DissolveTransform.TransformType,
            Options = new Dictionary<string, string> { ["groupByFields"] = "region" }
        };

        // Two adjacent unit squares in region "A" union into one 2x1 polygon;
        // a separate square in region "B" stays its own group.
        var features = Many(
            new Feature(Square(0, 0, 1), Attrs(("region", "A"))),
            new Feature(Square(1, 0, 1), Attrs(("region", "A"))),
            new Feature(Square(10, 10, 1), Attrs(("region", "B"))));

        var results = await Collect(transform.TransformAsync(config, features));

        results.Should().HaveCount(2);
        var groupA = results.Single(f => Equals(f.Attributes!["region"], "A"));
        groupA.Geometry!.Area.Should().BeApproximately(2.0, 1e-6);
        groupA.Geometry.SRID.Should().Be(4326);
        var groupB = results.Single(f => Equals(f.Attributes!["region"], "B"));
        groupB.Geometry!.Area.Should().BeApproximately(1.0, 1e-6);
    }

    [UnitTest]
    public async Task Dissolve_AllWhenNoGroupField_CollapsesToSingleFeature()
    {
        var transform = new DissolveTransform();
        var config = new TransformConfig
        {
            Type = DissolveTransform.TransformType,
            Options = new Dictionary<string, string>()
        };

        var features = Many(
            new Feature(Square(0, 0, 1), Attrs(("region", "A"))),
            new Feature(Square(5, 5, 1), Attrs(("region", "B"))));

        var results = await Collect(transform.TransformAsync(config, features));

        results.Should().ContainSingle();
        // Two disjoint squares union into a MultiPolygon with combined area 2.
        results.Single().Geometry!.Area.Should().BeApproximately(2.0, 1e-6);
    }

    [UnitTest]
    public async Task Dissolve_ComputesEachStatisticPerGroup()
    {
        var transform = new DissolveTransform();
        var config = new TransformConfig
        {
            Type = DissolveTransform.TransformType,
            Options = new Dictionary<string, string>
            {
                ["groupByFields"] = "region",
                ["statistics"] = ":count;pop:sum;pop:mean;pop:min;pop:max;name:first"
            }
        };

        var features = Many(
            new Feature(Square(0, 0, 1), Attrs(("region", "A"), ("pop", 10), ("name", "x"))),
            new Feature(Square(1, 0, 1), Attrs(("region", "A"), ("pop", 30), ("name", "y"))),
            new Feature(Square(10, 10, 1), Attrs(("region", "B"), ("pop", 5), ("name", "z"))));

        var results = await Collect(transform.TransformAsync(config, features));

        var groupA = results.Single(f => Equals(f.Attributes!["region"], "A"));
        Convert.ToInt64(groupA.Attributes!["COUNT"], CultureInfo.InvariantCulture).Should().Be(2);
        Convert.ToDouble(groupA.Attributes["SUM_pop"], CultureInfo.InvariantCulture).Should().Be(40.0);
        Convert.ToDouble(groupA.Attributes["MEAN_pop"], CultureInfo.InvariantCulture).Should().Be(20.0);
        Convert.ToDouble(groupA.Attributes["MIN_pop"], CultureInfo.InvariantCulture).Should().Be(10.0);
        Convert.ToDouble(groupA.Attributes["MAX_pop"], CultureInfo.InvariantCulture).Should().Be(30.0);
        groupA.Attributes["FIRST_name"].Should().Be("x");

        var groupB = results.Single(f => Equals(f.Attributes!["region"], "B"));
        Convert.ToInt64(groupB.Attributes!["COUNT"], CultureInfo.InvariantCulture).Should().Be(1);
        Convert.ToDouble(groupB.Attributes["SUM_pop"], CultureInfo.InvariantCulture).Should().Be(5.0);
    }

    [UnitTest]
    public async Task Dissolve_SingleFeatureGroup_PassesGeometryThrough()
    {
        var transform = new DissolveTransform();
        var config = new TransformConfig
        {
            Type = DissolveTransform.TransformType,
            Options = new Dictionary<string, string> { ["groupByFields"] = "region", ["statistics"] = ":count" }
        };

        var results = await Collect(transform.TransformAsync(
            config,
            Many(new Feature(Square(0, 0, 2), Attrs(("region", "solo"))))));

        results.Should().ContainSingle();
        results.Single().Geometry!.Area.Should().BeApproximately(4.0, 1e-6);
        Convert.ToInt64(results.Single().Attributes!["COUNT"], CultureInfo.InvariantCulture).Should().Be(1);
    }

    [UnitTest]
    public async Task Dissolve_EmptyInput_ProducesNoFeatures()
    {
        var transform = new DissolveTransform();
        var config = new TransformConfig
        {
            Type = DissolveTransform.TransformType,
            Options = new Dictionary<string, string> { ["groupByFields"] = "region" }
        };

        var results = await Collect(transform.TransformAsync(config, Many()));

        results.Should().BeEmpty();
    }

    [UnitTest]
    public async Task Dissolve_DropsNullAndEmptyGeometries()
    {
        var transform = new DissolveTransform();
        var config = new TransformConfig
        {
            Type = DissolveTransform.TransformType,
            Options = new Dictionary<string, string> { ["statistics"] = ":count" }
        };

        var features = Many(
            new Feature(Square(0, 0, 1), new AttributesTable()),
            new Feature(Factory.CreatePoint(), new AttributesTable()));

        var results = await Collect(transform.TransformAsync(config, features));

        results.Should().ContainSingle();
        Convert.ToInt64(results.Single().Attributes!["COUNT"], CultureInfo.InvariantCulture).Should().Be(1);
    }

    [UnitTest]
    public async Task Dissolve_UnknownStatistic_Throws()
    {
        var transform = new DissolveTransform();
        var config = new TransformConfig
        {
            Type = DissolveTransform.TransformType,
            Options = new Dictionary<string, string> { ["statistics"] = "pop:median" }
        };

        var act = async () => await Collect(transform.TransformAsync(
            config,
            Many(new Feature(Square(0, 0, 1), Attrs(("pop", 1))))));

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
