// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.FeatureServer.Services;

internal static partial class GeoServicesPreparedAdaptersLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to convert GeoServices query parameters.")]
    public static partial void QueryParameterConversionFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to convert GeoServices edits.")]
    public static partial void EditParameterConversionFailed(ILogger logger, Exception exception);
}
