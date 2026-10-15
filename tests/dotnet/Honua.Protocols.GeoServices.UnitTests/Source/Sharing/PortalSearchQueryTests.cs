// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Portal.Domain;
using Honua.Protocols.GeoServices.Sharing;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Sharing;

/// <summary>
/// Portal <c>q</c> treats top-level OR as a disjunction and AND or a space as a conjunction.
/// </summary>
public sealed class PortalSearchQueryTests
{
    private static readonly PortalItem[] Items =
    [
        Item("roads", "Feature Service", "Roads"),
        Item("parcels", "Map Service", "Parcels"),
        Item("tiles", "Vector Tile Service", "Tiles"),
    ];

    [UnitTest]
    public void ApplyQuery_ParenthesizedTypeOr_ReturnsBothTypes()
    {
        var matched = SharingRestEndpoints.ApplyQuery(
            Items,
            "(type:\"Map Service\" OR type:\"Feature Service\")",
            []);

        matched.Select(item => item.Id).Should().Equal("roads", "parcels");
    }

    [UnitTest]
    public void ApplyQuery_TopLevelTypeOr_ReturnsTheUnion()
    {
        var matched = SharingRestEndpoints.ApplyQuery(
            Items,
            "type:\"Feature Service\" OR type:\"Vector Tile Service\"",
            []);

        matched.Select(item => item.Id).Should().Equal("roads", "tiles");
    }

    [UnitTest]
    public void ApplyQuery_FreeTextOr_ReturnsTheUnion()
    {
        var matched = SharingRestEndpoints.ApplyQuery(Items, "roads OR parcels", []);

        matched.Select(item => item.Id).Should().Equal("roads", "parcels");
    }

    [UnitTest]
    public void ApplyQuery_SpaceAndExplicitAnd_AreConjunctions()
    {
        SharingRestEndpoints.ApplyQuery(Items, "type:\"Feature Service\" roads", [])
            .Select(item => item.Id).Should().Equal("roads");
        SharingRestEndpoints.ApplyQuery(Items, "type:\"Map Service\" AND parcels", [])
            .Select(item => item.Id).Should().Equal("parcels");
        SharingRestEndpoints.ApplyQuery(Items, "roads AND tiles", []).Should().BeEmpty();
    }

    [UnitTest]
    public void ApplyQuery_InGroupOr_StillMatchesEitherValue()
    {
        var matched = SharingRestEndpoints.ApplyQuery(
            Items,
            "type:(\"Feature Service\" OR \"Map Service\")",
            []);

        matched.Select(item => item.Id).Should().Equal("roads", "parcels");
    }

    [UnitTest]
    public void ApplyQuery_UnmappedOwnerPredicate_IsOmitted()
    {
        var matched = SharingRestEndpoints.ApplyQuery(
            Items,
            "owner:nobody",
            new HashSet<string>(["roads"], StringComparer.Ordinal));

        matched.Select(item => item.Id).Should().Equal("roads");
    }

    private static PortalItem Item(string id, string type, string title) => new()
    {
        Id = id,
        Owner = "publisher",
        Type = type,
        Title = title,
        Url = "https://example.test/" + id,
        Access = PortalAccessLevels.Public,
    };
}
