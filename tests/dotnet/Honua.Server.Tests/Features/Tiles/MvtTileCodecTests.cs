// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Formats;

namespace Honua.Server.Tests.Features.Tiles;

/// <summary>
/// Validates the vector-tile codec the tile proofs depend on (honua-server#4421). The decoder is
/// the instrument every other tile assertion reads through, so it is checked here against a
/// byte sequence derived by hand from the Mapbox Vector Tile 2.1 wire specification — not against
/// the builder's own output, which would only prove the two agree with each other.
/// </summary>
[Trait("Tier", "Fast")]
public sealed class MvtTileCodecTests
{
    /// <summary>
    /// One layer named <c>l</c>, extent 4096, one POINT feature at (5, 6) with <c>name=a</c>,
    /// encoded field by field from the specification:
    /// <code>
    /// 1A 22                            Tile.layers, length 34
    ///   78 02                          Layer.version = 2
    ///   0A 01 6C                       Layer.name = "l"
    ///   12 0D                          Layer.features, length 13
    ///     08 01                        Feature.id = 1
    ///     12 02 00 00                  Feature.tags = [0, 0]  (keys[0] -> values[0])
    ///     18 01                        Feature.type = POINT
    ///     22 03 09 0A 0C               Feature.geometry = [MoveTo(1), zigzag(5), zigzag(6)]
    ///   1A 04 6E 61 6D 65              Layer.keys = ["name"]
    ///   22 03 0A 01 61                 Layer.values = [Value{ string_value = "a" }]
    ///   28 80 20                       Layer.extent = 4096
    /// </code>
    /// </summary>
    private static readonly byte[] HandEncodedTile =
    [
        0x1A, 0x22,
        0x78, 0x02,
        0x0A, 0x01, 0x6C,
        0x12, 0x0D,
        0x08, 0x01,
        0x12, 0x02, 0x00, 0x00,
        0x18, 0x01,
        0x22, 0x03, 0x09, 0x0A, 0x0C,
        0x1A, 0x04, 0x6E, 0x61, 0x6D, 0x65,
        0x22, 0x03, 0x0A, 0x01, 0x61,
        0x28, 0x80, 0x20
    ];

    [Fact]
    public void Decode_HandEncodedTile_ReadsEveryFieldTheSpecificationDefines()
    {
        var tile = MvtTileDecoder.Decode(HandEncodedTile);

        var layer = tile.Layer("l");
        layer.Version.Should().Be(2);
        layer.Extent.Should().Be(4096);
        var feature = layer.Features.Should().ContainSingle().Subject;
        feature.Id.Should().Be(1);
        feature.GeometryType.Should().Be(MvtGeometryType.Point);
        feature.Attributes.Should().ContainKey("name").WhoseValue.Should().Be("a");
        var point = feature.Points.Should().ContainSingle().Subject;
        point.X.Should().Be(5, "zigzag(5) encodes as 0x0A");
        point.Y.Should().Be(6, "zigzag(6) encodes as 0x0C");
    }

    [Fact]
    public void Build_ProducesTheSameBytesAsTheHandEncodedTile()
    {
        // Pins the builder against the specification rather than against the decoder, so a shared
        // misreading of the wire format cannot hide inside a round trip.
        MvtTileBuilder.PointLayer("l", [(5, 6, "a")]).Should().Equal(HandEncodedTile);
    }

    [Fact]
    public void BuildThenDecode_PreservesEveryFeatureAndAttribute()
    {
        var payload = MvtTileBuilder.PointLayer(
            "roads",
            [(10, 20, "first"), (4000, 30, "second"), (0, 4095, "third")]);

        var layer = MvtTileDecoder.Decode(payload).Layer("roads");

        layer.Features.Should().HaveCount(3);
        layer.Features.Select(feature => feature.Attributes["name"])
            .Should().Equal("first", "second", "third");
        layer.Features[1].Points.Single().X.Should().Be(4000);
        layer.Features[2].Points.Single().Y.Should().Be(4095);
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04 })]
    [InlineData(new byte[] { 0x54, 0x50, 0x4B, 0x58 })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 })]
    public void Decode_JunkPayload_Fails(byte[] junk)
    {
        // The exact payloads the tile-job suites were pushing through the pipeline: four arbitrary
        // bytes, ASCII "TPKX", and a PNG magic prefix. None is a vector tile, and the decoder must
        // say so rather than yielding an empty-but-plausible result.
        MvtTileDecoder.TryDecode(junk, out var tile).Should().BeFalse();
        tile.Should().BeNull();
    }

    [Fact]
    public void Decode_EmptyPayload_Fails()
    {
        MvtTileDecoder.TryDecode([], out _).Should().BeFalse();
    }

    [Fact]
    public void Decode_TruncatedTile_Fails()
    {
        MvtTileDecoder.TryDecode(HandEncodedTile.AsSpan(0, HandEncodedTile.Length - 4).ToArray(), out _)
            .Should().BeFalse("a truncated payload must not decode as a shorter but valid tile");
    }
}
