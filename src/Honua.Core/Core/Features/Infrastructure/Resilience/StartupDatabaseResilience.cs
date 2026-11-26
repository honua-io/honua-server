// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Honua.Core.Features.Infrastructure.Resilience;

// A transient database outage starts degraded and retries; bad migration SQL or an incompatible PostGIS install still fails startup.
public static class StartupDatabaseResilience
{
    private static readonly string[] TransientSqlStates =
    [
        "57P01",
        "57P02",
        "57P03",
    ];

    public static bool IsTransientConnectivityError(Exception? exception)
    {
        if (exception is null)
        {
            return false;
        }

        // AggregateException fans out to multiple inner failures; treat the group as transient only
        // when every leaf is transient (a single permanent failure makes the whole thing fatal).
        // Check at every level of traversal so a wrapped AggregateException cannot hide a permanent
        // sibling behind its first inner exception.
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
            {
                return aggregate.InnerExceptions.All(IsTransientConnectivityError);
            }

            if (IsTransientSingle(current))
            {
                return true;
            }
        }

        return false;
    }

    // Request-path classifier: matches transient Npgsql connectivity/resource failures only so
    // non-database TimeoutException/SocketException and permanent TLS/protocol errors keep their
    // existing HTTP mappings.
    public static bool IsTransientDatabaseConnectivityError(Exception? exception)
    {
        if (exception is null)
        {
            return false;
        }

        // First walk for wrapped aggregates and authoritative server SQLSTATEs (e.g. an
        // NpgsqlException wrapping PostgresException 28P01 must stay non-transient).
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
            {
                return aggregate.InnerExceptions.All(IsTransientDatabaseConnectivityError);
            }

            if (IsPostgresException(current))
            {
                return IsTransientSqlState(TryReadSqlState(current));
            }
        }

        // No SQLSTATE: trust DbException.IsTransient on bare NpgsqlException so timeouts,
        // socket/IO failures, and pool exhaustion return 503 while TLS/certificate/protocol
        // failures (IsTransient == false) stay 500.
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (IsBareNpgsqlException(current))
            {
                return current is DbException dbException && dbException.IsTransient;
            }
        }

        return false;
    }

    private static bool IsTransientSingle(Exception exception)
    {
        // TimeoutException and socket failures are always transient on the startup path.
        var typeName = exception.GetType().FullName ?? exception.GetType().Name;
        if (typeName is "System.TimeoutException" or "System.Net.Sockets.SocketException")
        {
            return true;
        }

        return IsTransientNpgsqlException(exception);
    }

    private static bool IsTransientNpgsqlException(Exception exception)
    {
        if (IsPostgresException(exception))
        {
            return IsTransientSqlState(TryReadSqlState(exception));
        }

        if (IsBareNpgsqlException(exception))
        {
            var sqlState = TryReadSqlState(exception);
            return !string.IsNullOrEmpty(sqlState) ? IsTransientSqlState(sqlState) : true;
        }

        return false;
    }

    private static bool IsPostgresException(Exception exception)
        => (exception.GetType().FullName ?? exception.GetType().Name) == "Npgsql.PostgresException";

    private static bool IsBareNpgsqlException(Exception exception)
        => (exception.GetType().FullName ?? exception.GetType().Name) == "Npgsql.NpgsqlException";

    public static bool IsTransientSqlState(string? sqlState)
    {
        if (string.IsNullOrEmpty(sqlState))
        {
            return false;
        }

        // Class 08 — Connection Exception (connection_failure, sqlclient_unable_to_establish, etc.).
        // Class 53 — Insufficient Resources (too_many_connections, configuration_limit_exceeded, ...).
        if (sqlState.StartsWith("08", StringComparison.Ordinal)
            || sqlState.StartsWith("53", StringComparison.Ordinal))
        {
            return true;
        }

        return TransientSqlStates.Contains(sqlState, StringComparer.Ordinal);
    }

    // The description never includes the provider message, which can carry host names and ports.
    public static string DescribeFailureClass(Exception? exception)
    {
        if (exception is null)
        {
            return "unknown failure";
        }

        if (exception is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
        {
            return DescribeFailureClass(aggregate.InnerExceptions[0]);
        }

        string? fallback = null;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var typeName = current.GetType().FullName ?? current.GetType().Name;
            if (typeName is "Npgsql.PostgresException" && TryReadSqlState(current) is { Length: 5 } sqlState)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "SQLSTATE {0} ({1})",
                    sqlState,
                    DescribeSqlStateClass(sqlState));
            }

            if (current is TimeoutException)
            {
                fallback ??= "timeout";
            }
            else if (current is System.Net.Sockets.SocketException socketException)
            {
                fallback ??= string.Format(
                    CultureInfo.InvariantCulture,
                    "socket error ({0})",
                    socketException.SocketErrorCode);
            }
        }

        if (fallback is not null)
        {
            return fallback;
        }

        var outerName = exception.GetType().Name;
        return (exception.GetType().FullName ?? outerName).StartsWith("Npgsql.", StringComparison.Ordinal)
            ? "connection failure (" + outerName + ")"
            : "failure (" + outerName + ")";
    }

    private static string DescribeSqlStateClass(string sqlState)
    {
        var sqlClass = sqlState[..2];
        return sqlClass switch
        {
            "08" => "class 08: connection exception",
            "53" => "class 53: insufficient resources",
            "57" => "class 57: operator intervention",
            _ => "class " + sqlClass,
        };
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:'this' argument does not satisfy 'DynamicallyAccessedMemberTypes' in call to 'System.Type.GetProperty(String)'",
        Justification = "Real Npgsql exceptions inherit DbException and read DbException.SqlState directly without reflection; the GetProperty fallback is only for synthetic non-DbException test doubles and safely yields null if trimmed.")]
    private static string? TryReadSqlState(Exception exception)
    {
        if (exception is DbException dbException)
        {
            return dbException.SqlState;
        }

        var property = exception.GetType().GetProperty("SqlState");
        return property?.GetValue(exception) as string;
    }

    public static TimeSpan GetRetryDelay(
        int attempt,
        TimeSpan baseDelay,
        TimeSpan maxDelay,
        Random? jitter = null)
    {
        if (attempt < 1)
        {
            attempt = 1;
        }

        // Exponential growth, capped before jitter to avoid overflow on large attempt counts.
        var exponent = Math.Min(attempt - 1, 16);
        var scaled = baseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var cappedMs = Math.Min(scaled, maxDelay.TotalMilliseconds);

        if (jitter is not null)
        {
            // Full jitter: pick uniformly in [0, cappedMs]. Keeps retry storms from
            // synchronizing across many cold-starting instances.
            cappedMs *= jitter.NextDouble();
        }

        return TimeSpan.FromMilliseconds(Math.Max(0, cappedMs));
    }

    public static string BuildDegradedStartWarning(string phase, string errorMessage)
        => string.Format(
            CultureInfo.InvariantCulture,
            "Database {0} failed with a transient connectivity error and degraded-start is enabled: {1}. "
            + "The process is starting WITHOUT database access; non-database routes (e.g. /healthz/live, "
            + "static/proxy routes) will serve, /healthz/ready will report 503 until the database recovers, "
            + "and connectivity will be retried in the background. If this persists, treat it as an outage.",
            phase,
            errorMessage);
}
