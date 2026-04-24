// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Protocols.Ogc.Api.Tiles;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Tiles;

[Protocol(TestProtocols.OgcApiTiles)]
public sealed class TileRendererWkbTests
{
    private static readonly TileBounds TestBounds = new(0, 0, 256, 256);

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public void RenderTilePng_LittleEndianWkbPoint_ProducesNonEmptyImage()
    {
        var wkb = BuildPointWkb(128.0, 128.0, littleEndian: true);
        var feature = Feature.Create(1, wkb);
        var features = ImmutableArray.Create(feature);

        var png = TileRenderer.RenderTilePng(features, TestBounds, GeometryType.Point);

        png.Should().NotBeEmpty();
        png.Length.Should().BeGreaterThan(50); // PNG header alone is ~8 bytes; a drawn pixel adds more
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public void RenderTilePng_BigEndianWkbPoint_ProducesNonEmptyImage()
    {
        var wkb = BuildPointWkb(128.0, 128.0, littleEndian: false);
        var feature = Feature.Create(1, wkb);
        var features = ImmutableArray.Create(feature);

        var png = TileRenderer.RenderTilePng(features, TestBounds, GeometryType.Point);

        png.Should().NotBeEmpty();
        png.Length.Should().BeGreaterThan(50);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public void RenderTilePng_BigAndLittleEndian_ProduceSameImage()
    {
        var lePng = TileRenderer.RenderTilePng(
            ImmutableArray.Create(Feature.Create(1, BuildPointWkb(128.0, 128.0, littleEndian: true))),
            TestBounds, GeometryType.Point);

        var bePng = TileRenderer.RenderTilePng(
            ImmutableArray.Create(Feature.Create(1, BuildPointWkb(128.0, 128.0, littleEndian: false))),
            TestBounds, GeometryType.Point);

        // Both should produce identical rendered output
        lePng.Should().Equal(bePng);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public void RenderTilePng_BigAndLittleEndianMultiPoint_ProduceSameImage()
    {
        var littleEndian = TileRenderer.RenderTilePng(
            ImmutableArray.Create(Feature.Create(1, BuildMultiPointWkb([(64d, 64d), (192d, 192d)], littleEndian: true))),
            TestBounds,
            GeometryType.MultiPoint);

        var bigEndian = TileRenderer.RenderTilePng(
            ImmutableArray.Create(Feature.Create(1, BuildMultiPointWkb([(64d, 64d), (192d, 192d)], littleEndian: false))),
            TestBounds,
            GeometryType.MultiPoint);

        littleEndian.Should().Equal(bigEndian);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public void RenderTilePng_PolygonHole_RendersDifferentlyFromSolidPolygon()
    {
        var polygonWithHole = TileRenderer.RenderTilePng(
            ImmutableArray.Create(Feature.Create(1, BuildPolygonWkb(
                [
                    [(32d, 32d), (224d, 32d), (224d, 224d), (32d, 224d), (32d, 32d)],
                    [(96d, 96d), (160d, 96d), (160d, 160d), (96d, 160d), (96d, 96d)]
                ]))),
            TestBounds,
            GeometryType.Polygon);

        var solidPolygon = TileRenderer.RenderTilePng(
            ImmutableArray.Create(Feature.Create(1, BuildPolygonWkb(
                [[(32d, 32d), (224d, 32d), (224d, 224d), (32d, 224d), (32d, 32d)]]))),
            TestBounds,
            GeometryType.Polygon);

        polygonWithHole.Should().NotEqual(solidPolygon);
    }

    /// <summary>
    /// Builds a WKB Point payload with the specified byte order.
    /// Layout: [byteOrder(1)] [type(4)] [x(8)] [y(8)] = 21 bytes
    /// </summary>
    private static byte[] BuildPointWkb(double x, double y, bool littleEndian)
    {
        var wkb = new byte[21];
        wkb[0] = (byte)(littleEndian ? 1 : 0);

        if (littleEndian)
        {
            BinaryPrimitives.WriteInt32LittleEndian(wkb.AsSpan(1), 1); // Point type
            BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(5), x);
            BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(13), y);
        }
        else
        {
            BinaryPrimitives.WriteInt32BigEndian(wkb.AsSpan(1), 1); // Point type
            BinaryPrimitives.WriteDoubleBigEndian(wkb.AsSpan(5), x);
            BinaryPrimitives.WriteDoubleBigEndian(wkb.AsSpan(13), y);
        }

        return wkb;
    }

    private static byte[] BuildMultiPointWkb((double X, double Y)[] points, bool littleEndian)
    {
        var childSize = 21;
        var wkb = new byte[1 + 4 + 4 + (points.Length * childSize)];
        var offset = 0;

        wkb[offset++] = (byte)(littleEndian ? 1 : 0);
        WriteInt32(wkb, ref offset, 4, littleEndian);
        WriteInt32(wkb, ref offset, points.Length, littleEndian);

        foreach (var point in points)
        {
            var pointWkb = BuildPointWkb(point.X, point.Y, littleEndian);
            pointWkb.CopyTo(wkb, offset);
            offset += pointWkb.Length;
        }

        return wkb;
    }

    private static byte[] BuildPolygonWkb((double X, double Y)[][] rings)
    {
        var size = 1 + 4 + 4;
        foreach (var ring in rings)
        {
            size += 4 + (ring.Length * 16);
        }

        var wkb = new byte[size];
        var offset = 0;
        wkb[offset++] = 1;
        WriteInt32(wkb, ref offset, 3, littleEndian: true);
        WriteInt32(wkb, ref offset, rings.Length, littleEndian: true);

        foreach (var ring in rings)
        {
            WriteInt32(wkb, ref offset, ring.Length, littleEndian: true);
            foreach (var point in ring)
            {
                BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(offset, 8), point.X);
                offset += 8;
                BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(offset, 8), point.Y);
                offset += 8;
            }
        }

        return wkb;
    }

    private static void WriteInt32(byte[] buffer, ref int offset, int value, bool littleEndian)
    {
        if (littleEndian)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), value);
        }
        else
        {
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset, 4), value);
        }

        offset += 4;
    }
}
