// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Portal.Domain;
using Honua.Protocols.GeoServices.Sharing;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Sharing;

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

    [UnitTest]
    public void IsAdmissibleSearchQuery_BoundsLengthAndParenthesisDepth()
    {
        var limit = PortalSearchQueryLimits.MaxDepth;

        PortalSearchQueryLimits.IsAdmissible(Nested(limit)).Should().BeTrue();
        PortalSearchQueryLimits.IsAdmissible(Nested(limit + 1)).Should().BeFalse();
        PortalSearchQueryLimits.IsAdmissible(new string('(', limit + 1)).Should().BeFalse();
        PortalSearchQueryLimits.IsAdmissible(new string('a', PortalSearchQueryLimits.MaxLength)).Should().BeTrue();
        PortalSearchQueryLimits.IsAdmissible(new string('a', PortalSearchQueryLimits.MaxLength + 1)).Should().BeFalse();
    }

    [UnitTest]
    public void IsAdmissibleSearchQuery_IgnoresParenthesesInsideQuotes()
    {
        var quoted = "title:\"" + new string('(', PortalSearchQueryLimits.MaxDepth + 1) + "\"";

        PortalSearchQueryLimits.IsAdmissible(quoted).Should().BeTrue();
    }

    [UnitTest]
    public void ApplyQuery_NestingPastLimit_IsRefusedBeforeEvaluation()
    {
        var apply = () => SharingRestEndpoints.ApplyQuery(Items, Nested(1000), []);

        apply.Should().Throw<ArgumentOutOfRangeException>();
    }

    [UnitTest]
    public void ApplyQuery_NestingAtLimit_StillMatches()
    {
        var matched = SharingRestEndpoints.ApplyQuery(
            Items, Nested(PortalSearchQueryLimits.MaxDepth, "type:\"Map Service\""), []);

        matched.Select(item => item.Id).Should().Equal("parcels");
    }

    private static string Nested(int depth, string leaf = "roads")
        => new string('(', depth) + leaf + new string(')', depth);

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
