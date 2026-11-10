// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.OData.Services;

internal static partial class ODataPreparedAdaptersLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid OData query parameters.")]
    public static partial void InvalidQueryParameters(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to convert OData query parameters.")]
    public static partial void QueryParameterConversionFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to convert OData edit request.")]
    public static partial void EditParameterConversionFailed(ILogger logger, Exception exception);
}
