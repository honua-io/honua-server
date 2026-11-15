// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Query;

internal static partial class QueryLog
{
    [LoggerMessage(EventId = 7648, Level = LogLevel.Warning, Message = "Large limit optimized from {OriginalLimit} to {OptimizedLimit}")]
    public static partial void LargeLimitOptimized(ILogger logger, int originalLimit, int optimizedLimit);

    [LoggerMessage(EventId = 7649, Level = LogLevel.Warning, Message = "Failed to translate filter expression, falling back to SQL fragment")]
    public static partial void FilterTranslationFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7650, Level = LogLevel.Warning, Message = "Failed to estimate result count for layer {LayerId}")]
    public static partial void EstimateResultCountFailed(ILogger logger, int layerId, Exception exception);
}
