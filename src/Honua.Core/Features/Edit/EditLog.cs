// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Edit;

internal static partial class EditLog
{
    [LoggerMessage(EventId = 7620, Level = LogLevel.Information, Message = "Registered edit adapter for protocol {Protocol} with request type {RequestType}")]
    public static partial void RegisteredEditAdapter(ILogger logger, string protocol, string requestType);

    [LoggerMessage(EventId = 7621, Level = LogLevel.Debug, Message = "Converting {Protocol} edit request to unified edit for layer {LayerId}")]
    public static partial void ConvertingEditRequest(ILogger logger, string protocol, int layerId);

    [LoggerMessage(EventId = 7622, Level = LogLevel.Debug, Message = "Executing unified edit for layer {LayerId} with protocol {Protocol}, {CreateCount} creates, {UpdateCount} updates, {DeleteCount} deletes")]
    public static partial void ExecutingUnifiedEdit(ILogger logger, int layerId, string protocol, int createCount, int updateCount, int deleteCount);

    [LoggerMessage(EventId = 7623, Level = LogLevel.Error, Message = "Failed to execute unified edit for layer {LayerId}")]
    public static partial void ExecuteUnifiedEditFailed(ILogger logger, int layerId, Exception exception);

    [LoggerMessage(EventId = 7624, Level = LogLevel.Debug, Message = "Executing batch edit for {Protocol} with {RequestCount} requests on layer {LayerId}")]
    public static partial void ExecutingBatchEdit(ILogger logger, string protocol, int requestCount, int layerId);

    [LoggerMessage(EventId = 7625, Level = LogLevel.Error, Message = "Failed to execute batch edit for layer {LayerId}")]
    public static partial void ExecuteBatchEditFailed(ILogger logger, int layerId, Exception exception);

    [LoggerMessage(EventId = 7626, Level = LogLevel.Warning, Message = "Failed to estimate edit performance for resource {ResourceId}")]
    public static partial void EstimateEditPerformanceFailed(ILogger logger, string resourceId, Exception exception);

    [LoggerMessage(EventId = 7627, Level = LogLevel.Error, Message = "Failed to validate edit request for layer {LayerId}")]
    public static partial void ValidateEditRequestFailed(ILogger logger, int layerId, Exception exception);

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
