// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.StaticMap;

internal static partial class StaticMapLog
{
    [LoggerMessage(
        EventId = 5700,
        Level = LogLevel.Information,
        Message = "StaticMap requested: {ServiceId} {Width}x{Height} format={Format} dpi={Dpi}")]
    public static partial void Requested(ILogger logger, string serviceId, int width, int height, string format, int dpi);

    [LoggerMessage(
        EventId = 5701,
        Level = LogLevel.Information,
        Message = "StaticMap completed: {ServiceId} rendered {FeatureCount} features in {ElapsedMs}ms")]
    public static partial void Completed(ILogger logger, string serviceId, int featureCount, double elapsedMs);

    [LoggerMessage(
        EventId = 5702,
        Level = LogLevel.Error,
        Message = "StaticMap failed: {ServiceId}: {ErrorMessage}")]
    public static partial void Failed(ILogger logger, string serviceId, string errorMessage, Exception? exception = null);

}
