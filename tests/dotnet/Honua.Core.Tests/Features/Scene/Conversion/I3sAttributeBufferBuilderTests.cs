// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
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
