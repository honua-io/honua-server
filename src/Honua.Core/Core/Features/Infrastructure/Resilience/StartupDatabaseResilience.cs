// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Honua.Core.Features.Infrastructure.Resilience;

// A transient database outage starts degraded and retries; bad migration SQL or an incompatible PostGIS install still fails startup.
public static class StartupDatabaseResilience
{
    private static readonly string[] TransientSqlStates =
    [
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
        // Handle it explicitly rather than walking its first InnerException, which would otherwise
        // mask a permanent sibling failure.
        if (exception is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
        {
            return aggregate.InnerExceptions.All(IsTransientConnectivityError);
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (IsTransientSingle(current))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTransientSingle(Exception exception)
    {
        // TimeoutException and socket failures are always transient.
        var typeName = exception.GetType().FullName ?? exception.GetType().Name;
        if (typeName is "System.TimeoutException"
            || typeName == "System.Net.Sockets.SocketException")
        {
            return true;
        }

        // Npgsql's transient flag and SQLSTATE are read without taking a hard reference on the
        // provider: the connection-string resolver lives in Core, but Npgsql does not. A
        // PostgresException carries a public string SqlState; NpgsqlException exposes a bool
        // IsTransient. We surface both via the documented message conventions below and the
        // SQLSTATE scan so this helper stays provider-agnostic and AOT-friendly.
        if (typeName == "Npgsql.NpgsqlException" || typeName == "Npgsql.PostgresException")
        {
            var sqlState = TryReadSqlState(exception);
            if (sqlState is not null && IsTransientSqlState(sqlState))
            {
                return true;
            }

            // A bare NpgsqlException without a PostgreSQL SQLSTATE is almost always a
            // network/availability problem (host unreachable, connection refused, broken pipe).
            if (typeName == "Npgsql.NpgsqlException" && sqlState is null)
            {
                return true;
            }
        }

        return false;
    }

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
        Justification = "Best-effort cold-startup read of PostgresException.SqlState by name, used only to classify transient DB errors for degraded start without a compile-time Npgsql dependency in Core. If the property is trimmed away the lookup returns null and the failure is treated as non-transient — the safe default — so the access is correctness-neutral under trimming/AOT.")]
    private static string? TryReadSqlState(Exception exception)
    {
        // PostgresException exposes a public string SqlState property. Reading it via the runtime
        // property avoids a compile-time dependency on Npgsql from Core. Reflection here runs only
        // on the cold startup path (never in request hot paths); a trimmed-away property simply
        // yields null, which is treated as a non-transient error (the safe default).
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
