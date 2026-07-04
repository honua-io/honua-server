// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.HealthCheck;

internal static partial class HealthEndpointsLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to retrieve performance metrics")]
    public static partial void PerformanceMetricsFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to calculate the basic performance score; reporting a fallback score of 0")]
    public static partial void PerformanceScoreCalculationFailed(ILogger logger, Exception exception);
}
