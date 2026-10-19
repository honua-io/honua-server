// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Rendering;
using Honua.Protocols.Ogc.Classic.Wms;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

/// <summary>
/// Unit coverage for the wrap-aware GetFeatureInfo pixel-to-map math (#2739).
/// </summary>
public sealed class WmsGetFeatureInfoWrapTests
{
    [UnitTest]
    public void ComputeGetFeatureInfoMapX_NonWrappedExtent_IsLinear()
    {
        // -10..10 (width 20) over a 100px image: the center pixel maps to longitude ~0.
        var extent = new SkiaMapRenderer.RenderExtent(-10.0, -5.0, 10.0, 5.0);

        var mapX = WmsRequestHandlers.ComputeGetFeatureInfoMapX(extent, pixelX: 49.5, imageWidth: 100);

        mapX.Should().BeApproximately(0.0, 0.01);
    }

    [UnitTest]
    public void ComputeGetFeatureInfoMapX_AntimeridianCrossingExtent_MapsCenterToDateline()
    {
        // WMS 1.3.0 BBOX=-10,170,10,-170 (lat,lon) parses to a wrapped geographic extent
        // (MinX=170, MaxX=-170). Its effective width is 20 degrees across the antimeridian, so
        // the center column (I=180 of a 360px image) resolves to ~+/-180, NOT ~0 as the old
        // MaxX-MinX (negative width) math produced.
        var extent = new SkiaMapRenderer.RenderExtent(170.0, -10.0, -170.0, 10.0);

        var mapX = WmsRequestHandlers.ComputeGetFeatureInfoMapX(extent, pixelX: 180, imageWidth: 360);

        Math.Abs(mapX).Should().BeApproximately(180.0, 0.1);
        mapX.Should().BeInRange(-180.0, 180.0);
    }
}
