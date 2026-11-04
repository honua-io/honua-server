// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Snowflake.Features.FeatureStore.Services;

internal static partial class SnowflakeFeatureLog
{
    [LoggerMessage(
        EventId = 7200,
        Level = LogLevel.Debug,
        Message = "Snowflake feature query prepared with {ParameterCount} parameter(s).")]
    public static partial void QueryPrepared(ILogger logger, int parameterCount);

    [LoggerMessage(
        EventId = 7201,
        Level = LogLevel.Warning,
        Message = "Snowflake feature provider rejected unsupported operation '{Operation}' for layer {LayerId}.")]
    public static partial void OperationRejected(ILogger logger, string operation, int layerId);

    [LoggerMessage(
        EventId = 7202,
        Level = LogLevel.Error,
        Message = "Snowflake {OperationType} query failed for layer {LayerId}.")]
    public static partial void QueryFailed(ILogger logger, string operationType, int layerId, Exception exception);
}
