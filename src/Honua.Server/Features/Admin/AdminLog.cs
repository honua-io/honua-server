// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

/// <summary>
/// Source-generated logger for Admin features (AOT compatible)
/// </summary>
internal static partial class AdminLog
{
    /// <summary>
    /// Log when connection not found for table discovery
    /// </summary>
    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Warning,
        Message = "No connection string found for connection ID {ConnectionId}")]
    public static partial void ConnectionNotFound(ILogger logger, string connectionId);

    /// <summary>
    /// Log successful table discovery
    /// </summary>
    [LoggerMessage(
        EventId = 4002,
        Level = LogLevel.Information,
        Message = "Successfully discovered {Count} tables for connection {ConnectionId}")]
    public static partial void TableDiscoverySuccessful(ILogger logger, int count, string connectionId);

    /// <summary>
    /// Log table discovery failure
    /// </summary>
    [LoggerMessage(
        EventId = 4003,
        Level = LogLevel.Error,
        Message = "Failed to discover tables for connection {ConnectionId}")]
    public static partial void TableDiscoveryFailed(ILogger logger, Exception ex, string connectionId);

    /// <summary>
    /// Log PostGIS table discovery completion
    /// </summary>
    [LoggerMessage(
        EventId = 4004,
        Level = LogLevel.Information,
        Message = "Discovered {Count} PostGIS tables")]
    public static partial void PostGisTablesDiscovered(ILogger logger, int count);

    /// <summary>
    /// Log PostGIS table discovery error
    /// </summary>
    [LoggerMessage(
        EventId = 4005,
        Level = LogLevel.Error,
        Message = "Error discovering PostGIS tables")]
    public static partial void PostGisDiscoveryError(ILogger logger, Exception ex);
}
