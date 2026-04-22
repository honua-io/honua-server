// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Query;

internal static partial class QueryLog
{
    [LoggerMessage(EventId = 7640, Level = LogLevel.Information, Message = "Registered query adapter for protocol {Protocol} with parameter type {ParamType}")]
    public static partial void RegisteredQueryAdapter(ILogger logger, string protocol, string paramType);

    [LoggerMessage(EventId = 7641, Level = LogLevel.Debug, Message = "Converting {Protocol} parameters to unified query for layer {LayerId}")]
    public static partial void ConvertingQueryParameters(ILogger logger, string protocol, int layerId);

    [LoggerMessage(EventId = 7642, Level = LogLevel.Debug, Message = "Executing unified query for layer {LayerId} with protocol {Protocol}")]
    public static partial void ExecutingUnifiedQuery(ILogger logger, int layerId, string protocol);

    [LoggerMessage(EventId = 7643, Level = LogLevel.Error, Message = "Failed to execute unified query for layer {LayerId}")]
    public static partial void ExecuteUnifiedQueryFailed(ILogger logger, int layerId, Exception exception);

    [LoggerMessage(EventId = 7644, Level = LogLevel.Debug, Message = "Executing count query for layer {LayerId} with protocol {Protocol}")]
    public static partial void ExecutingCountQuery(ILogger logger, int layerId, string protocol);

    [LoggerMessage(EventId = 7645, Level = LogLevel.Error, Message = "Failed to execute count query for layer {LayerId}")]
    public static partial void ExecuteCountQueryFailed(ILogger logger, int layerId, Exception exception);

    [LoggerMessage(EventId = 7646, Level = LogLevel.Warning, Message = "Failed to build cache key for layer {LayerId}")]
    public static partial void BuildCacheKeyFailed(ILogger logger, int layerId, Exception exception);

    [LoggerMessage(EventId = 7647, Level = LogLevel.Warning, Message = "Failed to determine streaming preference for layer {LayerId}")]
    public static partial void DetermineStreamingPreferenceFailed(ILogger logger, int layerId, Exception exception);

    [LoggerMessage(EventId = 7648, Level = LogLevel.Warning, Message = "Large limit optimized from {OriginalLimit} to {OptimizedLimit}")]
    public static partial void LargeLimitOptimized(ILogger logger, int originalLimit, int optimizedLimit);

    [LoggerMessage(EventId = 7649, Level = LogLevel.Warning, Message = "Failed to translate filter expression, falling back to SQL fragment")]
    public static partial void FilterTranslationFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7650, Level = LogLevel.Warning, Message = "Failed to estimate result count for layer {LayerId}")]
    public static partial void EstimateResultCountFailed(ILogger logger, int layerId, Exception exception);
}
