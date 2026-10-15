// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Db.SqlServer.Tests;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class SqlServerFeatureQueryTotalCountTests
{
    [Fact]
    public void QueryAsync_LimitOneOverThreeRows_DoesNotReportTotalCountAsPageLength()
    {
        var total = FeatureQueryPageTotals.ResolveReportedTotalCount(
            returnedCount: 1,
            hasMore: true,
            offset: 0,
            exactMatchCount: 3);

        total.Should().Be(3);
    }

    [Fact]
    public void ResolveReportedTotalCount_CompleteFirstPage_UsesPageLength()
    {
        FeatureQueryPageTotals.ResolveReportedTotalCount(3, hasMore: false, offset: 0, exactMatchCount: 99)
            .Should().Be(3);
    }

    [Fact]
    public void ResolveReportedTotalCount_LaterPage_UsesExactMatchCount()
    {
        FeatureQueryPageTotals.ResolveReportedTotalCount(1, hasMore: false, offset: 2, exactMatchCount: 3)
            .Should().Be(3);
    }
}
