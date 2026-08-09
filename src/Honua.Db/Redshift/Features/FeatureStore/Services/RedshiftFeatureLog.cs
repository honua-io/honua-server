// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Redshift.Features.FeatureStore.Services;

/// <summary>
/// Source-generated structured logging for the Redshift feature store. AOT-safe; never
/// emits raw exception messages, connection strings, or user-supplied SQL fragments.
/// </summary>
internal static partial class RedshiftFeatureLog
{
    [LoggerMessage(
        EventId = 7200,
        Level = LogLevel.Debug,
        Message = "Redshift feature query prepared with {ParameterCount} parameter(s).")]
    public static partial void QueryPrepared(ILogger logger, int parameterCount);

    [LoggerMessage(
        EventId = 7201,
        Level = LogLevel.Warning,
        Message = "Redshift feature provider rejected unsupported operation '{Operation}' for layer {LayerId}.")]
    public static partial void OperationRejected(ILogger logger, string operation, int layerId);

    [LoggerMessage(
        EventId = 7202,
        Level = LogLevel.Error,
        Message = "Redshift {OperationType} query failed for layer {LayerId}.")]
    public static partial void QueryFailed(ILogger logger, string operationType, int layerId, Exception exception);
}
