// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Routing.Features.Routing.Providers;

internal static partial class PgRoutingProviderLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Routing stop {StopIndex} could not be snapped to a routing vertex.")]
    public static partial void StopNotSnapped(this ILogger logger, int stopIndex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "No route found between vertices {Source} and {Target}.")]
    public static partial void NoRouteBetweenVertices(this ILogger logger, long source, long target);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Service-area facility {FacilityIndex} could not be snapped to a vertex.")]
    public static partial void FacilityNotSnapped(this ILogger logger, int facilityIndex);
}
