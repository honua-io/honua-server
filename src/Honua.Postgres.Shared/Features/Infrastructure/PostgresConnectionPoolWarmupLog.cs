// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Infrastructure;

internal static partial class PostgresConnectionPoolWarmupLog
{
    [LoggerMessage(EventId = 8720, Level = LogLevel.Information, Message = "Starting thermal-optimized connection pool with base {ConnectionCount} connections")]
    public static partial void StartingConnectionPool(ILogger logger, int connectionCount);

    [LoggerMessage(EventId = 8721, Level = LogLevel.Information, Message = "Thermal-optimized connection pool startup completed in {ElapsedMs}ms")]
    public static partial void StartupCompleted(ILogger logger, double elapsedMs);

    [LoggerMessage(EventId = 8722, Level = LogLevel.Information, Message = "Thermal connection pool startup cancelled")]
    public static partial void StartupCancelled(ILogger logger);

    [LoggerMessage(EventId = 8723, Level = LogLevel.Warning, Message = "Thermal connection pool startup encountered issues but will continue (elapsed: {ElapsedMs}ms)")]
    public static partial void StartupIssues(ILogger logger, double elapsedMs, Exception exception);

    [LoggerMessage(EventId = 8724, Level = LogLevel.Debug, Message = "Thermal connection pool service stopping")]
    public static partial void ServiceStopping(ILogger logger);

    [LoggerMessage(EventId = 8725, Level = LogLevel.Debug, Message = "Thermal zones optimized: Hot={HotConnections}, Warm={WarmConnections}, Cold={ColdConnections}")]
    public static partial void ThermalZonesOptimized(ILogger logger, int hotConnections, int warmConnections, int coldConnections);

    [LoggerMessage(EventId = 8726, Level = LogLevel.Warning, Message = "Error during thermal zone optimization")]
    public static partial void ThermalZoneOptimizationError(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 8727, Level = LogLevel.Information, Message = "Usage pattern analysis: Peak times detected at {PeakHours}, predictability={Predictability:F2}")]
    public static partial void UsagePatternAnalysis(ILogger logger, string peakHours, double predictability);

    [LoggerMessage(EventId = 8728, Level = LogLevel.Warning, Message = "Error during usage pattern analysis")]
    public static partial void UsagePatternAnalysisError(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 8729, Level = LogLevel.Debug, Message = "Warming up connection #{ConnectionNumber}")]
    public static partial void WarmingUpConnection(ILogger logger, int connectionNumber);

    [LoggerMessage(EventId = 8730, Level = LogLevel.Debug, Message = "Connection #{ConnectionNumber} warmed up successfully")]
    public static partial void ConnectionWarmedUp(ILogger logger, int connectionNumber);

    [LoggerMessage(EventId = 8731, Level = LogLevel.Warning, Message = "Failed to warm up connection #{ConnectionNumber}")]
    public static partial void WarmupConnectionFailed(ILogger logger, int connectionNumber, Exception exception);

    [LoggerMessage(EventId = 8732, Level = LogLevel.Information, Message = "Predictive pre-warming triggered: Expected load increase={LoadIncrease:F2}")]
    public static partial void PredictivePrewarmingTriggered(ILogger logger, double loadIncrease);

    [LoggerMessage(EventId = 8733, Level = LogLevel.Warning, Message = "Error during predictive pre-warming")]
    public static partial void PredictivePrewarmingError(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 8734, Level = LogLevel.Debug, Message = "Initializing thermal zones")]
    public static partial void InitializingThermalZones(ILogger logger);

    [LoggerMessage(EventId = 8735, Level = LogLevel.Information, Message = "Thermal zones initialized: {HotCount} hot connections ready")]
    public static partial void ThermalZonesInitialized(ILogger logger, int hotCount);

    [LoggerMessage(EventId = 8736, Level = LogLevel.Information, Message = "Predictive pre-warming completed: {AdditionalHot} hot, {AdditionalWarm} warm connections")]
    public static partial void PredictivePrewarmingCompleted(ILogger logger, int additionalHot, int additionalWarm);

    [LoggerMessage(EventId = 8737, Level = LogLevel.Warning, Message = "Failed to create hot zone connection")]
    public static partial void CreateHotZoneConnectionFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 8738, Level = LogLevel.Warning, Message = "Failed to create warm zone connection")]
    public static partial void CreateWarmZoneConnectionFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 8739, Level = LogLevel.Debug, Message = "Usage pattern tracking started")]
    public static partial void UsagePatternTrackingStarted(ILogger logger);

    [LoggerMessage(EventId = 8740, Level = LogLevel.Debug, Message = "Usage pattern tracking stopped")]
    public static partial void UsagePatternTrackingStopped(ILogger logger);

    [LoggerMessage(EventId = 8741, Level = LogLevel.Debug, Message = "Pattern insights updated: Predictability={Predictability:F2}")]
    public static partial void PatternInsightsUpdated(ILogger logger, double predictability);
}
