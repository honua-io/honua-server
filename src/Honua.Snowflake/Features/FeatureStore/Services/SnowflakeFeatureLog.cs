// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Snowflake.Features.FeatureStore.Services;

/// <summary>
/// Source-generated structured logging for the Snowflake feature store. AOT-safe; never emits raw
/// exception messages, connection strings, or user-supplied SQL fragments.
/// </summary>
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
