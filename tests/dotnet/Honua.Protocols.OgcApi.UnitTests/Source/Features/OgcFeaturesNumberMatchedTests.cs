// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.Ogc.Api.Features;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class OgcFeaturesNumberMatchedTests
{
    [Fact]
    public void IsPageSizedTotal_TruncatedPage_RequiresMatchCountInsteadOfPageLength()
    {
        OgcFeaturesQueryHandler.IsPageSizedTotal(
            reportedTotal: 10,
            numberReturned: 10,
            hasMore: true,
            offset: 0).Should().BeTrue();
    }

    [Fact]
    public void IsPageSizedTotal_ExactTotalLargerThanThePage_IsTrusted()
    {
        OgcFeaturesQueryHandler.IsPageSizedTotal(
            reportedTotal: 30,
            numberReturned: 10,
            hasMore: true,
            offset: 0).Should().BeFalse();
    }

    [Fact]
    public void IsPageSizedTotal_CompleteFirstPage_IsTrusted()
    {
        OgcFeaturesQueryHandler.IsPageSizedTotal(
            reportedTotal: 10,
            numberReturned: 10,
            hasMore: false,
            offset: 0).Should().BeFalse();
    }

    [Fact]
    public void IsPageSizedTotal_OffsetPageReportingItsOwnLength_RequiresMatchCount()
    {
        OgcFeaturesQueryHandler.IsPageSizedTotal(
            reportedTotal: 10,
            numberReturned: 10,
            hasMore: false,
            offset: 10).Should().BeTrue();
    }
}
