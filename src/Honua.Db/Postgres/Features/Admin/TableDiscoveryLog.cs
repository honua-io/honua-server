// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Db.Postgres.Features.Admin;

internal static partial class TableDiscoveryLog
{
    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Information,
        Message = "Discovered {Count} PostGIS tables")]
    public static partial void PostGisTablesDiscovered(ILogger logger, int count);

    [LoggerMessage(
        EventId = 4102,
        Level = LogLevel.Error,
        Message = "Error discovering PostGIS tables")]
    public static partial void PostGisDiscoveryError(ILogger logger, Exception ex);

    [LoggerMessage(
        EventId = 4103,
        Level = LogLevel.Warning,
        Message = "Failed to estimate row count for {Schema}.{TableName}")]
    public static partial void RowCountEstimateFailed(ILogger logger, string schema, string tableName, Exception ex);

    [LoggerMessage(
        EventId = 4104,
        Level = LogLevel.Warning,
        Message = "Failed to discover columns for {Schema}.{TableName}")]
    public static partial void ColumnDiscoveryFailed(ILogger logger, string schema, string tableName, Exception ex);
}
