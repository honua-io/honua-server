// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Infrastructure.Rendering;

internal static partial class RasterMapRenderingPipelineLog
{
    [LoggerMessage(
        EventId = 3470,
        Level = LogLevel.Warning,
        Message = "PostGIS extent transform failed from SRID {FromSrid} to {ToSrid}")]
    public static partial void PostGisExtentTransformFailed(
        ILogger logger,
        int fromSrid,
        int toSrid,
        Exception exception);
}
