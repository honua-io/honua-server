// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.OgcTiles;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OgcTiles;

[Protocol(Protocols.OgcApiTiles)]
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
}
