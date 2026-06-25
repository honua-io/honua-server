// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.Conversion;

/// <summary>
/// Unit tests for the glTF/3D-Tiles → I3S geometry transcoder (#1810): verify
/// the Default interleaved buffer layout, vertex/feature counts, relative-to-MBS
/// recentring, and the feature-id back-mapping section.
/// </summary>
public sealed class I3sGeometryTranscoderTests
{
    [UnitTest]
    public void Transcode_FlatSquare_EmitsHeaderInterleavedStreamAndFeatureSection()
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
    public void Transcode_FeatureSection_MapsVertexRangeBackToObjectId()
    {
        // Two squares -> 12 vertices, the feature section must carry each
        // object id with its half-open [start, count) vertex range so an
        // identify flow can attribute a picked vertex back to a batch id.
        var features = new[] { Square(100), Square(200) };

        var result = I3sGeometryTranscoder.Transcode(features);

        result.VertexCount.Should().Be(12);
        result.FeatureCount.Should().Be(2);

        var featureSectionOffset = I3sGeometryTranscoder.HeaderBytes
            + (result.VertexCount * I3sGeometryTranscoder.VertexStrideBytes);

        // Feature 0: id 100, vertices [0, 6).
        BinaryPrimitives.ReadUInt64LittleEndian(result.Buffer.AsSpan(featureSectionOffset, 8)).Should().Be(100u);
        BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(featureSectionOffset + 8, 4)).Should().Be(0u);
        BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(featureSectionOffset + 12, 4)).Should().Be(6u);

        // Feature 1: id 200, vertices [6, 6).
        var second = featureSectionOffset + I3sGeometryTranscoder.FeatureRecordBytes;
        BinaryPrimitives.ReadUInt64LittleEndian(result.Buffer.AsSpan(second, 8)).Should().Be(200u);
        BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(second + 8, 4)).Should().Be(6u);
        BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(second + 12, 4)).Should().Be(6u);
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

        // The first vertex's normal (bytes 12..24 of the first stride) is a unit
        // ECEF vector.
        var baseOffset = I3sGeometryTranscoder.HeaderBytes + 12;
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
