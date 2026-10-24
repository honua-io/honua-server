// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

internal static partial class GeoServicesPreparedAdaptersLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to convert GeoServices query parameters.")]
    public static partial void QueryParameterConversionFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to convert GeoServices edits.")]
    public static partial void EditParameterConversionFailed(ILogger logger, Exception exception);
}
