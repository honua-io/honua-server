// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Routing.Features.Routing.Providers;

/// <summary>
/// Source-generated logging for the pgRouting provider.
/// </summary>
internal static partial class PgRoutingProviderLog
{
    /// <summary>
    /// Logs that a route stop could not be snapped to a graph vertex.
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Routing stop {StopIndex} could not be snapped to a routing vertex.")]
    public static partial void StopNotSnapped(this ILogger logger, int stopIndex);

    /// <summary>
    /// Logs that no route was found between two snapped vertices.
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "No route found between vertices {Source} and {Target}.")]
    public static partial void NoRouteBetweenVertices(this ILogger logger, long source, long target);

    /// <summary>
    /// Logs that a service-area facility could not be snapped to a graph vertex.
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Service-area facility {FacilityIndex} could not be snapped to a vertex.")]
    public static partial void FacilityNotSnapped(this ILogger logger, int facilityIndex);
}
