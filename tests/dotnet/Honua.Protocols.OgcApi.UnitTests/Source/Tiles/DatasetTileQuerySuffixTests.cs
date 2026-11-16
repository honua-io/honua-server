// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.Ogc.Api.Tiles;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Tiles;

[Protocol(TestProtocols.OgcApiTiles)]
public sealed class DatasetTileQuerySuffixTests
{
    private const int Cap = 16;
    private static readonly string[] DuplicateAndCommaIds = ["0", "0", "a,b", "A"];
    private static readonly string[] ExpectedLeadingIds = ["0", "A", "1"];

    [UnitTest]
    public void BuildDatasetTileQuerySuffix_UnfilteredWithinCap_AdvertisesNoCollectionsParameter()
    {
        var ids = Enumerable.Range(0, Cap).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();

        TilesEndpoints.BuildDatasetTileQuerySuffix(null, ids, Cap).Should().BeEmpty();
        TilesEndpoints.DescribeDatasetTiles(null, ids.Length, Cap).Should().Be("Dataset tiles across 16 collections.");
    }

    [UnitTest]
    public void BuildDatasetTileQuerySuffix_UnfilteredOverCap_AdvertisesFirstCapCollectionsInOrder()
    {
        var ids = Enumerable.Range(0, 20).Select(i => $"c {i}").ToArray();

        var suffix = TilesEndpoints.BuildDatasetTileQuerySuffix("  ", ids, Cap);

        suffix.Should().StartWith("?collections=");
        var advertised = suffix["?collections=".Length..].Split(',');
        advertised.Should().Equal(ids.Take(Cap).Select(Uri.EscapeDataString));
        TilesEndpoints.DescribeDatasetTiles(null, ids.Length, Cap).Should().Contain("first 16").And.Contain("20 collections");
    }

    [UnitTest]
    public void BuildDatasetTileQuerySuffix_UnfilteredOverCap_SkipsDuplicateAndCommaIdsBeforeTakingCap()
    {
        var ids = DuplicateAndCommaIds
            .Concat(Enumerable.Range(1, 20).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();

        var advertised = TilesEndpoints.BuildDatasetTileQuerySuffix(null, ids, Cap)["?collections=".Length..].Split(',');

        advertised.Should().HaveCount(Cap).And.OnlyHaveUniqueItems();
        advertised.Should().StartWith(ExpectedLeadingIds);
        advertised.Should().NotContain(Uri.EscapeDataString("a,b"));
    }

    [UnitTest]
    public void BuildDatasetTileQuerySuffix_ExplicitSelection_KeepsEverySelectedCollection()
    {
        var ids = new[] { "0", "1" };

        TilesEndpoints.BuildDatasetTileQuerySuffix("1,0", ids, Cap).Should().Be("?collections=0,1");
        TilesEndpoints.DescribeDatasetTiles("1,0", ids.Length, Cap).Should().Be("Dataset tiles across 2 collections.");
    }
}
