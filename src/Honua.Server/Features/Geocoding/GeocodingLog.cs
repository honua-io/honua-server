// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Geocoding;

internal static partial class GeocodingLog
{
    [LoggerMessage(
        EventId = 9801,
        Level = LogLevel.Information,
        Message = "Geocode operation {Operation} completed using provider {Provider} with {ResultCount} results in {ElapsedMilliseconds} ms.")]
    public static partial void OperationCompleted(
        ILogger logger,
        string operation,
        string provider,
        int resultCount,
        double elapsedMilliseconds);

    [LoggerMessage(
        EventId = 9802,
        Level = LogLevel.Warning,
        Message = "Geocode operation {Operation} is not supported by provider {Provider}.")]
    public static partial void CapabilityNotSupported(
        ILogger logger,
        string operation,
        string provider);

    [LoggerMessage(
        EventId = 9803,
        Level = LogLevel.Error,
        Message = "Geocode operation {Operation} failed using provider {Provider}: {ErrorMessage}")]
    public static partial void OperationFailed(
        ILogger logger,
        string operation,
        string provider,
        string errorMessage,
        Exception exception);
}

internal sealed class GeocodingLogCategory
{
}
