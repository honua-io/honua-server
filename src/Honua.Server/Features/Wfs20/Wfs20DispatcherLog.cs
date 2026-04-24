// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Wfs20;

internal static partial class Wfs20DispatcherLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid WFS 2.0 XML request body")]
    public static partial void InvalidXmlRequestBody(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to process WFS 2.0 request")]
    public static partial void WfsRequestFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DescribeFeatureType validation failed")]
    public static partial void DescribeFeatureTypeValidationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to process DescribeFeatureType request")]
    public static partial void DescribeFeatureTypeRequestFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to process GetFeature request")]
    public static partial void GetFeatureRequestFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to process GetPropertyValue request")]
    public static partial void GetPropertyValueRequestFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to process Transaction request")]
    public static partial void TransactionRequestFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to process ListStoredQueries request")]
    public static partial void ListStoredQueriesRequestFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to process DescribeStoredQueries request")]
    public static partial void DescribeStoredQueriesRequestFailed(ILogger logger, Exception exception);
}
