// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.TemporalHistory;

/// <summary>
/// Source-generated, AOT-safe structured log events for temporal-history reads and rollback execution.
/// </summary>
internal static partial class TemporalHistoryLog
{
    [LoggerMessage(EventId = 7100, Level = LogLevel.Debug,
        Message = "temporal.capability: layerId={LayerId} sourceKind={SourceKind} asOf={SupportsAsOf} diff={SupportsDiff}")]
    public static partial void Capability(ILogger logger, long layerId, string sourceKind, bool supportsAsOf, bool supportsDiff);

    [LoggerMessage(EventId = 7101, Level = LogLevel.Information,
        Message = "temporal.asof.query: layerId={LayerId} cursor={Cursor} featureCount={FeatureCount} elapsedMs={ElapsedMs}")]
    public static partial void AsOfQuery(ILogger logger, long layerId, string cursor, int featureCount, long elapsedMs);

    [LoggerMessage(EventId = 7102, Level = LogLevel.Information,
        Message = "temporal.diff.query: layerId={LayerId} from={FromCursor} to={ToCursor} added={Added} removed={Removed} changed={Changed} elapsedMs={ElapsedMs}")]
    public static partial void DiffQuery(ILogger logger, long layerId, string fromCursor, string toCursor, int added, int removed, int changed, long elapsedMs);

    [LoggerMessage(EventId = 7103, Level = LogLevel.Information,
        Message = "temporal.rollback.plan: layerId={LayerId} to={ToCursor} mode={Mode} affectedCount={AffectedCount}")]
    public static partial void RollbackPlan(ILogger logger, long layerId, string toCursor, string mode, int affectedCount);

    [LoggerMessage(EventId = 7104, Level = LogLevel.Information,
        Message = "temporal.rollback.execute: layerId={LayerId} jobId={JobId} to={ToCursor} applied={AppliedCount} checkpoint={Checkpoint}")]
    public static partial void RollbackExecute(ILogger logger, long layerId, string jobId, string toCursor, int appliedCount, string checkpoint);
}
