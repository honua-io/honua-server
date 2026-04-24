// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.OgcFeatures.Services;

internal static partial class OgcFeaturesPreparedAdaptersLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to convert OGC API Features query parameters.")]
    public static partial void QueryParameterConversionFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to convert OGC API Features edit request.")]
    public static partial void EditParameterConversionFailed(ILogger logger, Exception exception);
}
