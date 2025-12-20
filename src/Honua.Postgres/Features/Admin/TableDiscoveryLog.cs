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
}
