// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.FeatureStore;

// Bounded bbox pages past the first one (next links) keep the scoped serial plan.
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

    [IntegrationTest]
    public async Task RawPointPage_NextLinkPage_AppliesScopedPlannerSetting()
    {
        var page = await CreateReader().QueryGeoServicesRawPointPageAsync(1, Bbox() with { Offset = 1 });
        page.Items.Should().ContainSingle().Which.Id.Should().Be(2);
        page.Items.Select(RawSetting).Should().OnlyContain(value => value == "0");
        await AssertPoolSettingAsync();
    }
}
