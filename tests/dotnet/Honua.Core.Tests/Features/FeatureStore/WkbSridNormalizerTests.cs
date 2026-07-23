// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using Honua.Core.Features.FeatureStore.Services;

namespace Honua.Core.Tests.Features.FeatureStore;

public sealed class WkbSridNormalizerTests
{
    [Theory]
    [InlineData(0xA0000001u, 3)]
    [InlineData(0x60000001u, 3)]
    [InlineData(0xE0000001u, 4)]
    public void RemoveEmbeddedSrid_PreservesDimensionalFlagsAndOrdinateBytes(uint rawType, int ordinates)
    {
        var ewkb = CreatePoint(rawType, ordinates);

        var result = WkbSridNormalizer.RemoveEmbeddedSrid(ewkb);

        Assert.Equal(rawType & ~0x20000000u, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(1)));
        Assert.Equal(ewkb.AsSpan(9).ToArray(), result.AsSpan(5).ToArray());
    }

    [Fact]
    public void RemoveEmbeddedSrid_RemovesNestedCollectionSridsWithoutChangingPayload()
    {
        var child = CreatePoint(0x20000001u, 2);
        var collection = new byte[1 + (3 * sizeof(uint)) + child.Length];
        collection[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(collection.AsSpan(1), 0x20000004u);
        BinaryPrimitives.WriteUInt32LittleEndian(collection.AsSpan(5), 4326u);
        BinaryPrimitives.WriteUInt32LittleEndian(collection.AsSpan(9), 1u);
        child.CopyTo(collection, 13);

        var result = WkbSridNormalizer.RemoveEmbeddedSrid(collection);

        Assert.Equal(collection.Length - (2 * sizeof(uint)), result.Length);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(1)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(5)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(10)));
    }

    [Fact]
    public void RemoveEmbeddedSrid_TruncatedInput_ReturnsOriginalReference()
    {
        var truncated = new byte[] { 1, 1, 0, 0, 0x20, 0xE6, 0x10 };

        var result = WkbSridNormalizer.RemoveEmbeddedSrid(truncated);

        Assert.Same(truncated, result);
    }

    [Fact]
    public void RemoveEmbeddedSrid_TrailingJunk_ReturnsOriginalReference()
    {
        var valid = CreatePoint(0x20000001u, 2);
        var withJunk = valid.Concat(new byte[] { 0xCA, 0xFE }).ToArray();

        var result = WkbSridNormalizer.RemoveEmbeddedSrid(withJunk);

        Assert.Same(withJunk, result);
    }

    [Fact]
    public void RemoveEmbeddedSrid_UnsupportedGeometryType_ReturnsOriginalReference()
    {
        var unsupported = new byte[] { 1, 99, 0, 0, 0 };

        var result = WkbSridNormalizer.RemoveEmbeddedSrid(unsupported);

        Assert.Same(unsupported, result);
    }

    [Fact]
    public void RemoveEmbeddedSrid_MismatchedCollectionChildType_ReturnsOriginalReference()
    {
        var childLineString = new byte[] { 1, 2, 0, 0, 0, 0, 0, 0, 0 };
        var multiPoint = new byte[1 + sizeof(uint) + sizeof(uint) + childLineString.Length];
        multiPoint[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(multiPoint.AsSpan(1), 4u);
        BinaryPrimitives.WriteUInt32LittleEndian(multiPoint.AsSpan(5), 1u);
        childLineString.CopyTo(multiPoint, 9);

        var result = WkbSridNormalizer.RemoveEmbeddedSrid(multiPoint);

        Assert.Same(multiPoint, result);
    }

    private static byte[] CreatePoint(uint rawType, int ordinates)
    {
        var ewkb = new byte[1 + sizeof(uint) + sizeof(uint) + (ordinates * sizeof(double))];
        ewkb[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(ewkb.AsSpan(1), rawType);
        BinaryPrimitives.WriteUInt32LittleEndian(ewkb.AsSpan(5), 4326u);
        for (var ordinate = 0; ordinate < ordinates; ordinate++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(
                ewkb.AsSpan(9 + (ordinate * sizeof(double))),
                ordinate + 0.25);
        }

        return ewkb;
    }
}
