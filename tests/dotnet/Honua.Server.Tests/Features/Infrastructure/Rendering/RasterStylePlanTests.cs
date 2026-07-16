// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Rendering;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.Rendering;

/// <summary>
/// Covers the zoom-independence invariant of the cached raster style plan (honua-server#2873).
/// </summary>
/// <remarks>
/// A <c>RasterStylePlan</c> is cached per layer and reused for every later request against that
/// layer, at any extent and image size — and therefore at any zoom. Anything the plan pre-resolves
/// must consequently be the same at every zoom, or the first request to build the plan silently
/// freezes a zoom-dependent value for all the rest.
/// </remarks>
[Trait("Component", "MapServer")]
public class RasterStylePlanTests
{
    private const string StaticCircleStyle =
        """[{"id":"a","type":"circle","paint":{"circle-radius":6,"circle-color":"#f00"}}]""";

    private const string ZoomDependentCircleStyle =
        """[{"id":"a","type":"circle","paint":{"circle-radius":["interpolate",["linear"],["zoom"],5,2,15,20],"circle-color":"#f00"}}]""";

    /// <summary>
    /// The fast path stays available for styles that resolve identically at every zoom.
    /// </summary>
    [UnitTest]
    public void BuildRasterStylePlanFromJson_StaticCircleStyle_PreResolvesTheFastPathStyle()
    {
        var plan = RasterMapRenderingPipeline.BuildRasterStylePlanFromJson(StaticCircleStyle);

        plan.SimpleCircleStyle.Should().NotBeNull();
        plan.SimpleCircleStyle!.Radius.Should().Be(6f);
    }

    /// <summary>
    /// The decisive guard: a zoom-dependent circle style must not be pre-resolved into the cached
    /// plan. Doing so would bake the zoom of whichever request built the plan into every subsequent
    /// request for that layer, so a radius ramp would render at a stale zoom rather than the
    /// request's own — a confident wrong picture of exactly the kind #2867/#2868 produced. Falling
    /// back to <see langword="null"/> sends the render through the per-request path where the real
    /// <see cref="RenderZoom"/> is known.
    /// </summary>
    [UnitTest]
    public void BuildRasterStylePlanFromJson_ZoomDependentCircleStyle_DoesNotPreResolveIntoTheCachedPlan()
    {
        var plan = RasterMapRenderingPipeline.BuildRasterStylePlanFromJson(ZoomDependentCircleStyle);

        plan.SimpleCircleStyle.Should().BeNull(
            "a zoom-dependent style must not be pre-resolved into a plan that is cached across zooms");
        plan.StyleLayers.Should().HaveCount(1, "the style itself still renders, just via the per-request path");
    }

    /// <summary>
    /// Building the plan must not throw for a zoom-dependent style even though no zoom exists at
    /// plan-build time: the style is declared ineligible for pre-resolution before any evaluation
    /// is attempted.
    /// </summary>
    [UnitTest]
    public void BuildRasterStylePlanFromJson_ZoomDependentCircleStyle_DoesNotThrow()
    {
        var act = () => RasterMapRenderingPipeline.BuildRasterStylePlanFromJson(ZoomDependentCircleStyle);

        act.Should().NotThrow();
    }

    /// <summary>
    /// Zoom is not a feature attribute, so it must not widen the attribute projection the plan
    /// queries for.
    /// </summary>
    [UnitTest]
    public void BuildRasterStylePlanFromJson_ZoomDependentStyle_ReferencesNoFeatureFields()
    {
        var plan = RasterMapRenderingPipeline.BuildRasterStylePlanFromJson(ZoomDependentCircleStyle);

        plan.ReferencedFields.Should().BeEmpty();
    }
}
