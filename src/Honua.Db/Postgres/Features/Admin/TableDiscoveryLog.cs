// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Admin;

/// <summary>
/// Source-generated logger for PostGIS table discovery.
/// </summary>
internal static partial class TableDiscoveryLog
{
    /// <summary>
    /// Log PostGIS table discovery completion.
    /// </summary>
    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Information,
        Message = "Discovered {Count} PostGIS tables")]
    public static partial void PostGisTablesDiscovered(ILogger logger, int count);

    /// <summary>
    /// Log PostGIS table discovery error.
    /// </summary>
    [LoggerMessage(
        EventId = 4102,
        Level = LogLevel.Error,
        Message = "Error discovering PostGIS tables")]
    public static partial void PostGisDiscoveryError(ILogger logger, Exception ex);

    /// <summary>
    /// Log failure to estimate row count for a table.
    /// </summary>
    [LoggerMessage(
        EventId = 4103,
        Level = LogLevel.Warning,
        Message = "Failed to estimate row count for {Schema}.{TableName}")]
    public static partial void RowCountEstimateFailed(ILogger logger, string schema, string tableName, Exception ex);

    /// <summary>
    /// Log failure to discover columns for a table.
    /// </summary>
    [LoggerMessage(
        EventId = 4104,
        Level = LogLevel.Warning,
        Message = "Failed to discover columns for {Schema}.{TableName}")]
    public static partial void ColumnDiscoveryFailed(ILogger logger, string schema, string tableName, Exception ex);
}
