// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Edit;

internal static partial class EditLog
{
    [LoggerMessage(EventId = 7626, Level = LogLevel.Warning, Message = "Failed to estimate edit performance for resource {ResourceId}")]
    public static partial void EstimateEditPerformanceFailed(ILogger logger, string resourceId, Exception exception);

    [LoggerMessage(EventId = 7628, Level = LogLevel.Error, Message = "Error validating edit request for resource {ResourceId}")]
    public static partial void ValidateEditFailed(ILogger logger, string resourceId, Exception exception);

    [LoggerMessage(EventId = 7629, Level = LogLevel.Debug, Message = "Optimized edit request for resource {ResourceId}: {OriginalOps} -> {OptimizedOps} operations")]
    public static partial void EditRequestOptimized(ILogger logger, string resourceId, int originalOps, int optimizedOps);

    [LoggerMessage(EventId = 7630, Level = LogLevel.Warning, Message = "Failed to optimize edit request for resource {ResourceId}, returning original")]
    public static partial void OptimizeEditFailed(ILogger logger, string resourceId, Exception exception);

    [LoggerMessage(EventId = 7631, Level = LogLevel.Error, Message = "Error converting unified edit request to feature edit batch for resource {ResourceId}")]
    public static partial void FeatureEditBatchConversionFailed(ILogger logger, string resourceId, Exception exception);

    [LoggerMessage(EventId = 7632, Level = LogLevel.Error, Message = "Error validating transaction {TransactionId} for resource {ResourceId}")]
    public static partial void ValidateTransactionFailed(ILogger logger, string transactionId, string resourceId, Exception exception);
}
