// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.Conversion;

/// <summary>
/// Unit tests for the glTF/3D-Tiles → I3S geometry transcoder (#1810): verify
/// the Default PerAttributeArray buffer layout, vertex/feature counts, relative-to-MBS
/// recentring, and the feature-id / faceRange section.
/// </summary>
public sealed class I3sGeometryTranscoderTests
{
    [UnitTest]
    public void Transcode_FlatSquare_EmitsHeaderContiguousArraysAndFeatureSection()
    {
        var features = new[] { Square(objectId: 42) };

        var result = I3sGeometryTranscoder.Transcode(features);

        // A flat 4-vertex ring fan-triangulates into 2 triangles = 6 vertices.
        result.VertexCount.Should().Be(6);
        result.FeatureCount.Should().Be(1);

        var expectedLength = I3sGeometryTranscoder.HeaderBytes
            + (6 * I3sGeometryTranscoder.VertexStrideBytes)
            + (1 * I3sGeometryTranscoder.FeatureRecordBytes);
        result.Buffer.Length.Should().Be(expectedLength);

        // Header: vertexCount, featureCount.
        BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(0, 4)).Should().Be(6u);
        BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(4, 4)).Should().Be(1u);
    }

    [UnitTest]
    public void Transcode_IsByteIdenticalAcrossRuns()
    {
        var a = I3sGeometryTranscoder.Transcode(new[] { Square(1) });
        var b = I3sGeometryTranscoder.Transcode(new[] { Square(1) });

        a.Buffer.Should().Equal(b.Buffer);
        a.MbsCenterEcef.Should().Equal(b.MbsCenterEcef);
    }

    [UnitTest]
    public void Transcode_FeatureSection_MapsInclusiveFaceRangeBackToObjectId()
    {
        // Two squares -> 12 vertices = 4 triangles. Ids are a contiguous array,
        // then faceRange is the inclusive [first, last] triangle index of each
        // feature (a six-vertex feature is faces [0, 1], not vertex span (0, 6)).
        var features = new[] { Square(100), Square(200) };

        var result = I3sGeometryTranscoder.Transcode(features);

        result.VertexCount.Should().Be(12);
        result.FeatureCount.Should().Be(2);

        var featureIdOffset = I3sGeometryTranscoder.HeaderBytes
            + (result.VertexCount * I3sGeometryTranscoder.VertexStrideBytes);
        var faceRangeOffset = featureIdOffset + (result.FeatureCount * I3sGeometryTranscoder.FeatureIdBytes);

        BinaryPrimitives.ReadUInt64LittleEndian(result.Buffer.AsSpan(featureIdOffset, 8)).Should().Be(100u);
        BinaryPrimitives.ReadUInt64LittleEndian(
            result.Buffer.AsSpan(featureIdOffset + I3sGeometryTranscoder.FeatureIdBytes, 8)).Should().Be(200u);

        // Feature 0 owns triangles [0, 1]; feature 1 owns triangles [2, 3].
        BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(faceRangeOffset, 4)).Should().Be(0u);
        BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(faceRangeOffset + 4, 4)).Should().Be(1u);
        var secondFace = faceRangeOffset + I3sGeometryTranscoder.FaceRangeBytes;
        BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(secondFace, 4)).Should().Be(2u);
        BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(secondFace + 4, 4)).Should().Be(3u);
    }

    [UnitTest]
    public void Transcode_VertexAttributesAreContiguousPerAttributeArrays()
    {
        var result = I3sGeometryTranscoder.Transcode(new[] { Square(1) });
        var vertexCount = result.VertexCount;

        var normalOffset = I3sGeometryTranscoder.HeaderBytes
            + (vertexCount * I3sGeometryTranscoder.PositionBytesPerVertex);
        var uvOffset = normalOffset + (vertexCount * I3sGeometryTranscoder.NormalBytesPerVertex);
        var colorOffset = uvOffset + (vertexCount * I3sGeometryTranscoder.Uv0BytesPerVertex);

        // The second position sits one position-record after the first, not one
        // interleaved 36-byte stride later.
        var secondPosition = I3sGeometryTranscoder.HeaderBytes + I3sGeometryTranscoder.PositionBytesPerVertex;
        Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(result.Buffer.AsSpan(secondPosition, 4)))
            .Should().BeLessThan(1000f);

        // uv0 is the zero placeholder; color is opaque white. Both are their own arrays.
        BinaryPrimitives.ReadSingleLittleEndian(result.Buffer.AsSpan(uvOffset, 4)).Should().Be(0f);
        BinaryPrimitives.ReadSingleLittleEndian(result.Buffer.AsSpan(uvOffset + 4, 4)).Should().Be(0f);
        result.Buffer[colorOffset].Should().Be(255);
        result.Buffer[colorOffset + 1].Should().Be(255);
        result.Buffer[colorOffset + 2].Should().Be(255);
        result.Buffer[colorOffset + 3].Should().Be(255);
    }

    [UnitTest]
    public void Transcode_DegenerateFeatureBesideRealGeometry_OmitsEmptyFaceRange()
    {
        var degenerate = new SceneFeature
        {
            Id = 7,
            Geometry = new SceneFeatureGeometry
            {
                Kind = SceneGeometryKind.Polygon,
                Vertices = new[]
                {
                    new SceneVertex(-122.42, 37.77, 10.0),
                    new SceneVertex(-122.42, 37.77, 10.0),
                },
            },
        };

        var result = I3sGeometryTranscoder.Transcode(new[] { degenerate, Square(9) });

        result.FeatureCount.Should().Be(1);
        result.VertexCount.Should().Be(6);
        var featureIdOffset = I3sGeometryTranscoder.HeaderBytes
            + (result.VertexCount * I3sGeometryTranscoder.VertexStrideBytes);
        BinaryPrimitives.ReadUInt64LittleEndian(result.Buffer.AsSpan(featureIdOffset, 8)).Should().Be(9u);
        var faceRangeOffset = featureIdOffset + I3sGeometryTranscoder.FeatureIdBytes;
        BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(faceRangeOffset, 4)).Should().Be(0u);
        BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(faceRangeOffset + 4, 4)).Should().Be(1u);
    }

    [UnitTest]
    public void Transcode_RecentersPositionsAboutMbsCenter()
    {
        var result = I3sGeometryTranscoder.Transcode(new[] { Square(1) });

        // The MBS centre is near the Earth's surface in ECEF (|c| ~ 6.37e6 m).
        var magnitude = Math.Sqrt(
            (result.MbsCenterEcef[0] * result.MbsCenterEcef[0])
            + (result.MbsCenterEcef[1] * result.MbsCenterEcef[1])
            + (result.MbsCenterEcef[2] * result.MbsCenterEcef[2]));
        magnitude.Should().BeApproximately(6.37e6, 5e4);

        // The relative position stream must therefore be small-magnitude (the
        // square is ~tens of metres across), not absolute ~6.3e6 ECEF.
        var firstX = BinaryPrimitives.ReadSingleLittleEndian(
            result.Buffer.AsSpan(I3sGeometryTranscoder.HeaderBytes, 4));
        Math.Abs(firstX).Should().BeLessThan(1000f);

        result.MbsRadiusMeters.Should().BeGreaterThan(0.0);
    }

    [UnitTest]
    public void Transcode_NormalsAreUnitLength()
    {
        var result = I3sGeometryTranscoder.Transcode(new[] { Square(1) });

        // Normals are their own array, immediately after every position.
        var baseOffset = I3sGeometryTranscoder.HeaderBytes
            + (result.VertexCount * I3sGeometryTranscoder.PositionBytesPerVertex);
        var nx = BinaryPrimitives.ReadSingleLittleEndian(result.Buffer.AsSpan(baseOffset, 4));
        var ny = BinaryPrimitives.ReadSingleLittleEndian(result.Buffer.AsSpan(baseOffset + 4, 4));
        var nz = BinaryPrimitives.ReadSingleLittleEndian(result.Buffer.AsSpan(baseOffset + 8, 4));
        var length = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        length.Should().BeApproximately(1.0, 1e-5);
    }

    [UnitTest]
    public void Transcode_NonPolygonKind_Throws()
    {
        var point = new SceneFeature
        {
            Id = 1,
            Geometry = new SceneFeatureGeometry
            {
                Kind = SceneGeometryKind.Point,
                Vertices = new[] { new SceneVertex(0, 0, null) },
            },
        };

        var act = () => I3sGeometryTranscoder.Transcode(new[] { point });

        act.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void Transcode_EmptyFeatures_Throws()
    {
        var act = () => I3sGeometryTranscoder.Transcode(Array.Empty<SceneFeature>());

        act.Should().Throw<ArgumentException>();
    }

    private static SceneFeature Square(long objectId) => new()
    {
        Id = objectId,
        Geometry = new SceneFeatureGeometry
        {
            Kind = SceneGeometryKind.Polygon,
            Vertices = new[]
            {
                new SceneVertex(-122.4200, 37.7700, 10.0),
                new SceneVertex(-122.4199, 37.7700, 10.0),
                new SceneVertex(-122.4199, 37.7701, 10.0),
                new SceneVertex(-122.4200, 37.7701, 10.0),
            },
        },
    };
}
