// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

internal static partial class Wfs20PreparedAdaptersLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to convert WFS query parameters.")]
    public static partial void QueryParameterConversionFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to convert WFS transaction operations.")]
    public static partial void TransactionConversionFailed(ILogger logger, Exception exception);
}
