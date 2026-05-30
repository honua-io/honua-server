// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Protocols.GeoServices.MapServer.Rendering;

/// <summary>
/// Backward-compatible wrapper over the shared raster render capacity limiter.
/// </summary>
internal sealed class RasterRenderCapacityLimiter : Infrastructure.Rendering.RasterRenderCapacityLimiter
{
    public RasterRenderCapacityLimiter()
    {
    }

    internal RasterRenderCapacityLimiter(
        int maxConcurrentRenders,
        long maxReservedBytes,
        TimeSpan? acquireTimeout = null)
        : base(maxConcurrentRenders, maxReservedBytes, acquireTimeout)
    {
    }
}
