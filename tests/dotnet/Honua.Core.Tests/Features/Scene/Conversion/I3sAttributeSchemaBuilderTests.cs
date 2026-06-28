// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;
using Honua.Scene;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.Conversion;

/// <summary>
/// Unit tests for the I3S attribute schema builder (#1811): the
/// <c>attributeStorageInfo</c> field list an ArcGIS SceneLayer client reads to
/// discover the identify-able fields. The schema must always lead with the
/// synthetic OBJECTID field, assign stable <c>f_{n}</c> keys in deterministic
/// order, and pick the value type that matches the served attribute file.
/// </summary>
public sealed class I3sAttributeSchemaBuilderTests
{
    [UnitTest]
    public void Build_NoAttributes_EmitsObjectIdFieldOnly()
    {
        var features = new[] { FeatureWith(1, new Dictionary<string, object?>()) };

        var schema = I3sAttributeSchemaBuilder.Build(features);

        schema.Should().HaveCount(1);
        schema[0].Key.Should().Be(I3sAttributeSchemaBuilder.ObjectIdFieldKey);
        schema[0].Name.Should().Be("OBJECTID");
        schema[0].AttributeValues!.ValueType.Should().Be(I3sAttributeBufferBuilder.Oid32ValueType);
    }

    [UnitTest]
    public void Build_TypesFields_NumericAsFloat64StringAsString_InOrdinalKeyOrder()
    {
        var features = new[]
        {
            FeatureWith(1, new Dictionary<string, object?> { ["height"] = 10.0, ["name"] = "a" }),
            FeatureWith(2, new Dictionary<string, object?> { ["name"] = "b", ["zone"] = 4 }),
        };

        var schema = I3sAttributeSchemaBuilder.Build(features);

        // OBJECTID first, then user keys sorted ordinally: height, name, zone.
        schema.Select(f => f.Name).Should().ContainInOrder("OBJECTID", "height", "name", "zone");
        schema.Select(f => f.Key).Should().ContainInOrder("f_0", "f_1", "f_2", "f_3");

        var height = schema.Single(f => f.Name == "height");
        height.AttributeValues!.ValueType.Should().Be(I3sAttributeBufferBuilder.Float64ValueType);

        var name = schema.Single(f => f.Name == "name");
        name.AttributeValues!.ValueType.Should().Be(I3sAttributeBufferBuilder.StringValueType);
        name.AttributeValues!.Encoding.Should().Be("UTF-8");
        name.AttributeByteCounts!.ValueType.Should().Be("UInt32");

        var zone = schema.Single(f => f.Name == "zone");
        zone.AttributeValues!.ValueType.Should().Be(I3sAttributeBufferBuilder.Float64ValueType);
    }

    [UnitTest]
    public void Build_MixedTypesAcrossFeatures_DemotesFieldToString()
    {
        // One feature reports a numeric value, another a non-numeric string for
        // the same key: the field must serve as a String so no value is lost.
        var features = new[]
        {
            FeatureWith(1, new Dictionary<string, object?> { ["code"] = 42 }),
            FeatureWith(2, new Dictionary<string, object?> { ["code"] = "N/A" }),
        };

        var schema = I3sAttributeSchemaBuilder.Build(features);

        var code = schema.Single(f => f.Name == "code");
        code.AttributeValues!.ValueType.Should().Be(I3sAttributeBufferBuilder.StringValueType);
    }

    private static SceneFeature FeatureWith(long id, IReadOnlyDictionary<string, object?> attributes) => new()
    {
        Id = id,
        Geometry = new SceneFeatureGeometry
        {
            Kind = SceneGeometryKind.Polygon,
            Vertices = new[]
            {
                new SceneVertex(-122.42, 37.77, 0.0),
                new SceneVertex(-122.41, 37.77, 0.0),
                new SceneVertex(-122.41, 37.78, 0.0),
            },
        },
        Attributes = attributes,
    };
}
