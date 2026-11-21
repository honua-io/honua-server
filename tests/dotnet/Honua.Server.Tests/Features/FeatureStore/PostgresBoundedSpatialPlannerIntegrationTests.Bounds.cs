// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.FeatureStore;

// Bounded bbox pages past the first one (next links) and pages of up to 1,000 rows
// keep the scoped serial plan.
// Pages beyond the bounds are covered by the automatic count/page planning tests.
public sealed partial class PostgresBoundedSpatialPlannerIntegrationTests
{
    [IntegrationTest]
    public async Task QueryPage_NextLinkPage_AppliesScopedPlannerSetting()
    {
        var page = await CreateReader().QueryPageAsync(1, Bbox() with { Offset = 1 });
        page.Items.Should().ContainSingle().Which.Id.Should().Be(2);
        page.Items.Select(Setting).Should().OnlyContain(value => value == "0");
        await AssertPoolSettingAsync();
    }

    [IntegrationTheory]
    [InlineData(101)]
    [InlineData(1000)]
    public async Task QueryPage_LargerPageWithinBounds_AppliesScopedPlannerSetting(int limit)
    {
        var page = await CreateReader().QueryPageAsync(1, Bbox() with { Limit = limit });
        page.Items.Select(feature => feature.Id).Should().Equal(1L, 2L);
        page.Items.Select(Setting).Should().OnlyContain(value => value == "0");
        await AssertPoolSettingAsync();
    }

    [IntegrationTest]
    public async Task RawPointPage_NextLinkPage_AppliesScopedPlannerSetting()
    {
        var page = await CreateReader().QueryGeoServicesRawPointPageAsync(1, Bbox() with { Offset = 1 });
        page.Items.Should().ContainSingle().Which.Id.Should().Be(2);
        page.Items.Select(RawSetting).Should().OnlyContain(value => value == "0");
        await AssertPoolSettingAsync();
    }

    [IntegrationTest]
    public async Task RawPointPage_PageOverBounds_RetainsOrdinaryPlannerSetting()
    {
        var page = await CreateReader().QueryGeoServicesRawPointPageAsync(1, Bbox() with { Limit = 1001 });
        page.Items.Select(feature => feature.Id).Should().Equal(1L, 2L);
        page.Items.Select(RawSetting).Should().OnlyContain(value => value == "2");
        await AssertPoolSettingAsync();
    }
}
