// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Text;
using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;
using Honua.Scene;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.Conversion;

/// <summary>
/// Unit tests for the I3S per-field attribute binary builder (#1811): the
/// <c>nodes/{id}/attributes/f_0/0</c> file that an ArcGIS SceneLayer client reads
/// to satisfy identify. The OBJECTID values must be derived from — and aligned
/// with — the served node geometry's feature section.
/// </summary>
public sealed class I3sAttributeBufferBuilderTests
{
    [UnitTest]
    public void Build_ObjectIdField_EmitsCountHeaderAndFeatureIdValues()
    {
        var geometry = TranscodeThreeFeatures();
        var field = ObjectIdField();

        var bytes = I3sAttributeBufferBuilder.Build(geometry, field);

        bytes.Should().NotBeNull();
        var span = bytes!.AsSpan();

        var expectedLength = I3sAttributeBufferBuilder.HeaderBytes
            + (geometry.FeatureCount * I3sAttributeBufferBuilder.Oid32ValueBytes);
        bytes.Length.Should().Be(expectedLength);

        var count = BinaryPrimitives.ReadUInt32LittleEndian(span[..4]);
        count.Should().Be((uint)geometry.FeatureCount);

        // Values must equal the source feature ids in feature order, so a picked
        // feature resolves to the same OBJECTID the geometry feature section maps.
        var id0 = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(I3sAttributeBufferBuilder.HeaderBytes, 4));
        var id1 = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(I3sAttributeBufferBuilder.HeaderBytes + 4, 4));
        var id2 = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(I3sAttributeBufferBuilder.HeaderBytes + 8, 4));
        id0.Should().Be(11);
        id1.Should().Be(22);
        id2.Should().Be(33);
    }

    [UnitTest]
    public void Build_NonObjectIdField_ReturnsNull()
    {
        // A user-attribute field is advertised but its values need the deferred
        // EXT_structural_metadata decode, so the builder declines to fabricate it.
        var geometry = TranscodeThreeFeatures();
        var field = new I3sAttributeStorageInfo
        {
            Key = "f_1",
            Name = "HEIGHT",
            Ordering = ["attributeValues"],
            AttributeValues = new I3sAttributeValues { ValueType = "Float64", ValuesPerElement = 1 },
        };

        var bytes = I3sAttributeBufferBuilder.Build(geometry, field);

        bytes.Should().BeNull();
    }

    [UnitTest]
    public void Build_ObjectIdKeyButWrongValueType_ReturnsNull()
    {
        var geometry = TranscodeThreeFeatures();
        var field = new I3sAttributeStorageInfo
        {
            Key = I3sAttributeBufferBuilder.ObjectIdFieldKey,
            Name = "OBJECTID",
            Ordering = ["attributeValues"],
            AttributeValues = new I3sAttributeValues { ValueType = "String", ValuesPerElement = 1 },
        };

        I3sAttributeBufferBuilder.Build(geometry, field).Should().BeNull();
    }

    [UnitTest]
    public void Build_FromFeatures_ObjectIdField_EmitsFeatureIdsInOrder()
    {
        var features = ThreeFeaturesWithAttributes();

        var bytes = I3sAttributeBufferBuilder.Build(features, I3sAttributeSchemaBuilder.BuildObjectIdField());

        bytes.Should().NotBeNull();
        var span = bytes!.AsSpan();
        BinaryPrimitives.ReadUInt32LittleEndian(span[..4]).Should().Be(3);
        BinaryPrimitives.ReadInt32LittleEndian(span.Slice(I3sAttributeBufferBuilder.HeaderBytes, 4)).Should().Be(11);
        BinaryPrimitives.ReadInt32LittleEndian(span.Slice(I3sAttributeBufferBuilder.HeaderBytes + 4, 4)).Should().Be(22);
        BinaryPrimitives.ReadInt32LittleEndian(span.Slice(I3sAttributeBufferBuilder.HeaderBytes + 8, 4)).Should().Be(33);
    }

    [UnitTest]
    public void Build_FromFeatures_Float64Field_EmitsCountHeaderAndDoubleValues()
    {
        var features = ThreeFeaturesWithAttributes();
        var field = NumericField("f_1", "HEIGHT");

        var bytes = I3sAttributeBufferBuilder.Build(features, field);

        bytes.Should().NotBeNull();
        var span = bytes!.AsSpan();
        var expectedLength = I3sAttributeBufferBuilder.HeaderBytes
            + (features.Count * I3sAttributeBufferBuilder.Float64ValueBytes);
        bytes!.Length.Should().Be(expectedLength);

        BinaryPrimitives.ReadUInt32LittleEndian(span[..4]).Should().Be(3);
        BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(I3sAttributeBufferBuilder.HeaderBytes, 8)).Should().Be(12.5);
        BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(I3sAttributeBufferBuilder.HeaderBytes + 8, 8)).Should().Be(7.0);
        // The third feature has no HEIGHT value, so it encodes as 0.
        BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(I3sAttributeBufferBuilder.HeaderBytes + 16, 8)).Should().Be(0.0);
    }

    [UnitTest]
    public void Build_FromFeatures_StringField_EmitsByteCountsAndNullTerminatedUtf8()
    {
        var features = ThreeFeaturesWithAttributes();
        var field = StringField("f_2", "NAME");

        var bytes = I3sAttributeBufferBuilder.Build(features, field);

        bytes.Should().NotBeNull();
        var span = bytes!.AsSpan();

        var count = BinaryPrimitives.ReadUInt32LittleEndian(span[..4]);
        count.Should().Be(3);
        var valuesByteCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));

        // Per-value byte counts include the trailing NUL: "alpha"=6, "beta"=5, ""=1.
        var byteCountsOffset = I3sAttributeBufferBuilder.StringHeaderBytes;
        BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(byteCountsOffset, 4)).Should().Be(6);
        BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(byteCountsOffset + 4, 4)).Should().Be(5);
        BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(byteCountsOffset + 8, 4)).Should().Be(1);
        valuesByteCount.Should().Be(6u + 5u + 1u);

        var valuesOffset = byteCountsOffset + (3 * I3sAttributeBufferBuilder.ByteCountValueBytes);
        var firstValue = Encoding.UTF8.GetString(span.Slice(valuesOffset, 5));
        firstValue.Should().Be("alpha");
        span[valuesOffset + 5].Should().Be(0);
    }

    [UnitTest]
    public void Build_FromFeatures_UnsupportedValueType_ReturnsNull()
    {
        var features = ThreeFeaturesWithAttributes();
        var field = new I3sAttributeStorageInfo
        {
            Key = "f_3",
            Name = "GEOM",
            AttributeValues = new I3sAttributeValues { ValueType = "Geometry" },
        };

        I3sAttributeBufferBuilder.Build(features, field).Should().BeNull();
    }

    // BH-S-03 regression: feature IDs > Int32.MaxValue (e.g. OSM building IDs) must not
    // silently truncate to negative/wrong values. Both Build overloads must throw before
    // packing any Oid32 value that does not fit in the 32-bit signed space.

    [UnitTest]
    public void Build_FromFeatures_ObjectIdExceedsInt32Max_Throws()
    {
        const ulong largeId = (ulong)int.MaxValue + 1;
        var features = new List<SceneFeature>
        {
            FeatureWith((long)largeId, new Dictionary<string, object?>()),
        };

        var act = () => I3sAttributeBufferBuilder.Build(features, I3sAttributeSchemaBuilder.BuildObjectIdField());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Oid32*");
    }

    [UnitTest]
    public void Build_FromGeometry_ObjectIdExceedsInt32Max_Throws()
    {
        // Transcode a feature whose ID encodes into the geometry buffer, then
        // attempt to read it back as an Oid32 attribute file.
        const long largeId = (long)int.MaxValue + 1;
        var features = new[]
        {
            new SceneFeature
            {
                Id = largeId,
                Geometry = Square(largeId, -122.42, 37.77).Geometry,
                Attributes = new Dictionary<string, object?>(),
            },
        };
        var geometry = I3sGeometryTranscoder.Transcode(features);

        var act = () => I3sAttributeBufferBuilder.Build(geometry, ObjectIdField());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Oid32*");
    }

    private static IReadOnlyList<SceneFeature> ThreeFeaturesWithAttributes() =>
    [
        FeatureWith(11, new Dictionary<string, object?> { ["HEIGHT"] = 12.5, ["NAME"] = "alpha" }),
        FeatureWith(22, new Dictionary<string, object?> { ["HEIGHT"] = 7, ["NAME"] = "beta" }),
        FeatureWith(33, new Dictionary<string, object?>()),
    ];

    private static SceneFeature FeatureWith(long id, IReadOnlyDictionary<string, object?> attributes) => new()
    {
        Id = id,
        Geometry = Square(id, -122.42, 37.77).Geometry,
        Attributes = attributes,
    };

    private static I3sAttributeStorageInfo NumericField(string key, string name) => new()
    {
        Key = key,
        Name = name,
        Ordering = ["attributeValues"],
        AttributeValues = new I3sAttributeValues { ValueType = I3sAttributeBufferBuilder.Float64ValueType, ValuesPerElement = 1 },
    };

    private static I3sAttributeStorageInfo StringField(string key, string name) => new()
    {
        Key = key,
        Name = name,
        Ordering = ["attributeByteCounts", "attributeValues"],
        AttributeValues = new I3sAttributeValues { ValueType = I3sAttributeBufferBuilder.StringValueType, Encoding = "UTF-8", ValuesPerElement = 1 },
    };

    private static I3sTranscodedGeometry TranscodeThreeFeatures()
    {
        var features = new[]
        {
            Square(11, -122.4200, 37.7700),
            Square(22, -122.4190, 37.7700),
            Square(33, -122.4180, 37.7700),
        };
        return I3sGeometryTranscoder.Transcode(features);
    }

    private static SceneFeature Square(long id, double lon, double lat) => new()
    {
        Id = id,
        Geometry = new SceneFeatureGeometry
        {
            Kind = SceneGeometryKind.Polygon,
            Vertices = new[]
            {
                new SceneVertex(lon, lat, 10.0),
                new SceneVertex(lon + 0.0001, lat, 10.0),
                new SceneVertex(lon + 0.0001, lat + 0.0001, 10.0),
                new SceneVertex(lon, lat + 0.0001, 10.0),
            },
        },
    };

    private static I3sAttributeStorageInfo ObjectIdField() => new()
    {
        Key = I3sAttributeBufferBuilder.ObjectIdFieldKey,
        Name = "OBJECTID",
        Ordering = ["attributeValues"],
        AttributeValues = new I3sAttributeValues
        {
            ValueType = I3sAttributeBufferBuilder.Oid32ValueType,
            ValuesPerElement = 1,
        },
    };
}
