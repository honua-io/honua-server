// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed class TopFeaturesPageLimitTests
{
    [UnitTheory]
    [InlineData(null, 2000, 2000)]
    [InlineData(0, 2000, 2000)]
    [InlineData(50, 2000, 50)]
    [InlineData(1_000_000, 2000, 2000)]
    [InlineData(10, 0, 1)]
    public void RecordCountIsBoundedByTheServiceCap(int? resultRecordCount, int maxRecordCount, int expected)
        => FeatureServerEndpoints.ResolveTopFeaturesPageLimit(resultRecordCount, maxRecordCount).Should().Be(expected);

    [UnitTest]
    public void AProbeRowPastThePageIsDroppedAndReported()
    {
        var page = FeatureServerEndpoints.TrimTopFeaturesPage(Features(3), pageLimit: 2);

        page.Items.Select(feature => feature.Id).Should().Equal(1L, 2L);
        page.HasMoreResults.Should().BeTrue();
    }

    [UnitTest]
    public void AShortPageIsReturnedAsRead()
    {
        var page = FeatureServerEndpoints.TrimTopFeaturesPage(Features(2), pageLimit: 2);

        page.Items.Should().HaveCount(2);
        page.HasMoreResults.Should().BeFalse();
    }

    private static QueryResult<Feature> Features(int count)
        => QueryResult<Feature>.Create(
            count,
            [.. Enumerable.Range(1, count).Select(id => Feature.Create(id, geometry: null))]);
}
