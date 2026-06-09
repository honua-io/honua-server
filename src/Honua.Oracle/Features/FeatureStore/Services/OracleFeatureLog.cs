// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Oracle.Features.FeatureStore.Services;

/// <summary>
/// Source-generated structured logging for the Oracle feature store. AOT-safe; never emits
/// raw exception messages, connection strings, or user-supplied SQL fragments.
/// </summary>
internal static partial class OracleFeatureLog
{
    [LoggerMessage(
        EventId = 7100,
        Level = LogLevel.Debug,
        Message = "Oracle feature query prepared with {ParameterCount} parameter(s).")]
    public static partial void QueryPrepared(ILogger logger, int parameterCount);

    [LoggerMessage(
        EventId = 7101,
        Level = LogLevel.Warning,
        Message = "Oracle feature provider rejected unsupported operation '{Operation}' for layer {LayerId}.")]
    public static partial void OperationRejected(ILogger logger, string operation, int layerId);

    [LoggerMessage(
        EventId = 7102,
        Level = LogLevel.Warning,
        Message = "Oracle feature provider rejected non-SDO geometry surface for layer {LayerId}: column '{Column}' on '{Table}' is typed '{DataType}'.")]
    public static partial void NonSdoSurfaceRejected(ILogger logger, int layerId, string column, string table, string dataType);

    [LoggerMessage(
        EventId = 7103,
        Level = LogLevel.Warning,
        Message = "Oracle feature provider rejected ArcSDE-versioned table for layer {LayerId}: '{Table}' carries versioning column(s) {Columns}.")]
    public static partial void VersionedTableRejected(ILogger logger, int layerId, string table, string columns);
}
