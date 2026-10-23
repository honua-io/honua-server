// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>Catalog IN operands must reach list evaluation, not scalar resolution.</summary>
public sealed class ImageServerCatalogMembershipTests
{
    [UnitTheory]
    [InlineData("OBJECTID IN (3)", new long[] { 3 })]
    [InlineData("OBJECTID NOT IN (3)", new long[] { 1, 2 })]
    [InlineData("OBJECTID IN (3, NULL)", new long[] { 3 })]
    [InlineData("OBJECTID NOT IN (3, NULL)", new long[] { })]
    [InlineData("NULL IN (NULL)", new long[] { })]
    [InlineData("NULL NOT IN (NULL)", new long[] { })]
    [InlineData("NOT (OBJECTID IN (99, NULL))", new long[] { })]
    [InlineData("NOT (NULL IN (NULL))", new long[] { })]
    [InlineData("OBJECTID IN (3, NULL) AND OBJECTID = 3", new long[] { 3 })]
    [InlineData("OBJECTID IN (3, NULL) OR Name = 'west'", new long[] { 1, 3 })]
    [InlineData("NOT (OBJECTID IN (3, NULL) AND OBJECTID = 3)", new long[] { 1, 2 })]
    [InlineData("NOT (OBJECTID IN (3, NULL) OR Name = 'west')", new long[] { })]
    [InlineData("OBJECTID IN (99)", new long[] { })]
    [InlineData("OBJECTID NOT IN (99)", new long[] { 1, 2, 3 })]
    [InlineData("(OBJECTID IN (3) AND Name LIKE 'e%') OR Name = 'west'", new long[] { 1, 3 })]
    public void Apply_MembershipAndMixedFilters_ReturnsExactIndependentItems(string where, long[] expected)
    {
        var actual = new ImageServerCatalogFilterEvaluator().Apply(Items(), where);
        actual.Select(item => item.ObjectId).Should().Equal(expected);
    }

    [UnitTheory]
    [InlineData("UnknownField IN (3)")]
    [InlineData("OBJECTID IN (UnknownField)")]
    [InlineData("OBJECTID IN (3) AND UnknownField = 1")]
    public void Apply_UnknownField_RemainsRejected(string where)
    {
        var action = () => new ImageServerCatalogFilterEvaluator().Apply(Items(), where);
        action.Should().Throw<ImageServerCatalogFilterException>();
    }

    [UnitTheory]
    [InlineData("OBJECTID IN (9007199254740993)", 9007199254740993L)]
    [InlineData("OBJECTID IN (9007199254740992.0)", 9007199254740992L)]
    [InlineData("OBJECTID IN (9.007199254740992e15)", 9007199254740992L)]
    [InlineData("OBJECTID IN ('9007199254740993')", 9007199254740993L)]
    public void Apply_LargeAdjacentObjectIds_PreservesExactMembership(string where, long expected)
    {
        var items = new[]
        {
            new ImageServerCatalogItem { ObjectId = 9007199254740992, Name = "a", PixelType = "8BUI" },
            new ImageServerCatalogItem { ObjectId = 9007199254740993, Name = "b", PixelType = "8BUI" }
        };
        new ImageServerCatalogFilterEvaluator().Apply(items, where)
            .Select(item => item.ObjectId).Should().Equal(expected);
    }

    private static ImageServerCatalogItem[] Items() =>
    [
        new() { ObjectId = 1, Name = "west", PixelType = "8BUI" },
        new() { ObjectId = 2, Name = "overlap", PixelType = "8BUI" },
        new() { ObjectId = 3, Name = "east", PixelType = "8BUI" }
    ];
}
