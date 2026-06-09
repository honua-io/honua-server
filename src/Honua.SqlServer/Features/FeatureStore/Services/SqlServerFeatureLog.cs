// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.SqlServer.Features.FeatureStore.Services;

/// <summary>
/// Source-generated structured logging for the SQL Server feature store. AOT-safe; never
/// emits raw exception messages, connection strings, or user-supplied SQL fragments.
/// </summary>
internal static partial class SqlServerFeatureLog
{
    [LoggerMessage(
        EventId = 7000,
        Level = LogLevel.Debug,
        Message = "SQL Server feature query prepared with {ParameterCount} parameter(s).")]
    public static partial void QueryPrepared(ILogger logger, int parameterCount);

    [LoggerMessage(
        EventId = 7001,
        Level = LogLevel.Warning,
        Message = "SQL Server feature provider rejected unsupported operation '{Operation}' for layer {LayerId}.")]
    public static partial void OperationRejected(ILogger logger, string operation, int layerId);
}
