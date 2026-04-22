// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Admin;

internal static partial class PerformanceBenchmarkLog
{
    [LoggerMessage(
        EventId = 8760,
        Level = LogLevel.Information,
        Message = "Performance benchmark for Layer {LayerId}: Features: {ActualCount}/{ExpectedCount}, Count: {CountTime}ms, Spatial: {SpatialTime}ms, Attributes: {AttributeTime}ms, Max: {MaxTime}ms, Target: {TargetTime}ms, Performant: {IsPerformant}")]
    public static partial void BenchmarkResult(
        ILogger logger,
        int layerId,
        long actualCount,
        int expectedCount,
        double countTime,
        double spatialTime,
        double attributeTime,
        double maxTime,
        double targetTime,
        bool isPerformant);

    [LoggerMessage(
        EventId = 8761,
        Level = LogLevel.Information,
        Message = "Index utilization for Layer {LayerId}: Spatial scans: {SpatialScans}, Attribute scans: {AttributeScans}, Spatial indexes: {SpatialCount}, Attribute indexes: {AttributeCount}")]
    public static partial void IndexUtilization(
        ILogger logger,
        int layerId,
        long spatialScans,
        long attributeScans,
        int spatialCount,
        int attributeCount);
}
