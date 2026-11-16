// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Rendering;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.Rendering;

[Trait("Component", "MapServer")]
public class RasterStylePlanTests
{
    private const string StaticCircleStyle =
        """[{"id":"a","type":"circle","paint":{"circle-radius":6,"circle-color":"#f00"}}]""";

    private const string ZoomDependentCircleStyle =
        """[{"id":"a","type":"circle","paint":{"circle-radius":["interpolate",["linear"],["zoom"],5,2,15,20],"circle-color":"#f00"}}]""";

    [UnitTest]
    public void BuildRasterStylePlanFromJson_StaticCircleStyle_PreResolvesTheFastPathStyle()
    {
        var plan = RasterMapRenderingPipeline.BuildRasterStylePlanFromJson(StaticCircleStyle);

        plan.SimpleCircleStyle.Should().NotBeNull();
        plan.SimpleCircleStyle!.Radius.Should().Be(6f);
    }

    [UnitTest]
    public void BuildRasterStylePlanFromJson_ZoomDependentCircleStyle_DoesNotPreResolveIntoTheCachedPlan()
    {
        var plan = RasterMapRenderingPipeline.BuildRasterStylePlanFromJson(ZoomDependentCircleStyle);

        plan.SimpleCircleStyle.Should().BeNull(
            "a zoom-dependent style must not be pre-resolved into a plan that is cached across zooms");
        plan.StyleLayers.Should().HaveCount(1, "the style itself still renders, just via the per-request path");
    }

    [UnitTest]
    public void BuildRasterStylePlanFromJson_ZoomDependentCircleStyle_DoesNotThrow()
    {
        var act = () => RasterMapRenderingPipeline.BuildRasterStylePlanFromJson(ZoomDependentCircleStyle);

        act.Should().NotThrow();
    }

    [UnitTest]
    public void BuildRasterStylePlanFromJson_ZoomDependentStyle_ReferencesNoFeatureFields()
    {
        var plan = RasterMapRenderingPipeline.BuildRasterStylePlanFromJson(ZoomDependentCircleStyle);

        plan.ReferencedFields.Should().BeEmpty();
    }
}
