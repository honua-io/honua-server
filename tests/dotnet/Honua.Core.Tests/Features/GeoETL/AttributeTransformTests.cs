// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.GeoETL.Domain;
using Honua.Core.Features.GeoETL.Services.Transforms;
using Honua.TestKit.Attributes;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;

namespace Honua.Core.Tests.Features.GeoETL;

/// <summary>
/// Unit coverage for the managed attribute transforms: rename, cast / type-coerce, and
/// the computed / calculated field transform. All run in-memory over NetTopologySuite
/// features with no native dependency.
/// </summary>
public sealed class AttributeTransformTests
{
    private static readonly GeometryFactory Factory = new(new PrecisionModel(), 4326);

    [UnitTest]
    public async Task Rename_MovesAttributeValueToNewKey()
    {
        var transform = new AttributeRenameTransform();
        var config = new TransformConfig
        {
            Type = AttributeRenameTransform.TransformType,
            Options = new Dictionary<string, string> { ["from"] = "old", ["to"] = "new" }
        };
        var feature = new Feature(Factory.CreatePoint(new Coordinate(0, 0)),
            new AttributesTable { { "old", "value" }, { "keep", 1L } });

        var result = (await Collect(transform.TransformAsync(config, Single(feature)))).Single();

        result.Attributes!.Exists("old").Should().BeFalse();
        result.Attributes!.GetOptionalValue("new").Should().Be("value");
        result.Attributes!.GetOptionalValue("keep").Should().Be(1L);
    }

    [UnitTest]
    public async Task Cast_CoercesStringToDouble()
    {
        var transform = new AttributeCastTransform();
        var config = new TransformConfig
        {
            Type = AttributeCastTransform.TransformType,
            Options = new Dictionary<string, string> { ["field"] = "n", ["to"] = "double" }
        };
        var feature = new Feature(Factory.CreatePoint(new Coordinate(0, 0)),
            new AttributesTable { { "n", "42.5" } });

        var result = (await Collect(transform.TransformAsync(config, Single(feature)))).Single();

        result.Attributes!.GetOptionalValue("n").Should().Be(42.5d);
    }

    [UnitTest]
    public async Task Cast_DropsUncoercibleRowByDefault()
    {
        var transform = new AttributeCastTransform();
        var config = new TransformConfig
        {
            Type = AttributeCastTransform.TransformType,
            Options = new Dictionary<string, string> { ["field"] = "n", ["to"] = "int" }
        };
        var good = new Feature(Factory.CreatePoint(new Coordinate(0, 0)),
            new AttributesTable { { "n", "10" } });
        var bad = new Feature(Factory.CreatePoint(new Coordinate(1, 1)),
            new AttributesTable { { "n", "not-a-number" } });

        var results = await Collect(transform.TransformAsync(config, Many(good, bad)));

        results.Should().HaveCount(1);
        results.Single().Attributes!.GetOptionalValue("n").Should().Be(10);
    }

    [UnitTest]
    public async Task ComputedField_AddsArithmeticResult()
    {
        var transform = new ComputedFieldTransform();
        var config = new TransformConfig
        {
            Type = ComputedFieldTransform.TransformType,
            Options = new Dictionary<string, string>
            {
                ["target"] = "area", ["op"] = "multiply", ["left"] = "w", ["right"] = "h"
            }
        };
        var feature = new Feature(Factory.CreatePoint(new Coordinate(0, 0)),
            new AttributesTable { { "w", 3L }, { "h", 4L } });

        var result = (await Collect(transform.TransformAsync(config, Single(feature)))).Single();

        result.Attributes!.GetOptionalValue("area").Should().Be(12d);
    }

    [UnitTest]
    public async Task ComputedField_ConcatJoinsFieldsWithSeparator()
    {
        var transform = new ComputedFieldTransform();
        var config = new TransformConfig
        {
            Type = ComputedFieldTransform.TransformType,
            Options = new Dictionary<string, string>
            {
                ["target"] = "full", ["op"] = "concat", ["fields"] = "first,last", ["separator"] = " "
            }
        };
        var feature = new Feature(Factory.CreatePoint(new Coordinate(0, 0)),
            new AttributesTable { { "first", "Ada" }, { "last", "Lovelace" } });

        var result = (await Collect(transform.TransformAsync(config, Single(feature)))).Single();

        result.Attributes!.GetOptionalValue("full").Should().Be("Ada Lovelace");
    }

    [UnitTest]
    public async Task ComputedField_DropsRowOnDivideByZero()
    {
        var transform = new ComputedFieldTransform();
        var config = new TransformConfig
        {
            Type = ComputedFieldTransform.TransformType,
            Options = new Dictionary<string, string>
            {
                ["target"] = "ratio", ["op"] = "divide", ["left"] = "n", ["right"] = "d"
            }
        };
        var feature = new Feature(Factory.CreatePoint(new Coordinate(0, 0)),
            new AttributesTable { { "n", 1L }, { "d", 0L } });

        var results = await Collect(transform.TransformAsync(config, Single(feature)));

        results.Should().BeEmpty();
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

    private static async IAsyncEnumerable<IFeature> Single(IFeature feature)
    {
        yield return feature;
        await Task.CompletedTask;
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
