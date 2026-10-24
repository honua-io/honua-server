// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Db.Postgres.Features.AnomalyDetection;

/// <summary>
/// Structured logging for anomaly detection operations.
/// Event ID range: 7900-7949.
/// </summary>
internal static partial class AnomalyLog
{
    [LoggerMessage(
        EventId = 7900,
        Level = LogLevel.Information,
        Message = "Anomaly analysis for {Layer}: {GeomCount} geometry, {AttrCount} attribute anomalies in {Count} features")]
    public static partial void AnalysisCompleted(
        ILogger logger, string layer, int geomCount, int attrCount, long count);
}
