// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Infrastructure.Resilience;

/// <summary>
/// Pure, provider-agnostic helpers that classify database startup failures and compute
/// retry backoff for degraded-start mode.
/// </summary>
/// <remarks>
/// On serverless hosts (e.g. AWS Lambda) a failed initialization aborts the runtime
/// (<c>Runtime.ExitError</c>) and becomes a crash loop, returning 500s on every route —
/// even routes that never touch the database. When the unavailability is a <em>transient</em>
/// connectivity problem (pool exhaustion, the database still warming up, a momentary socket
/// failure) the process should start in a degraded mode and retry in the background rather than
/// crash. Genuine misconfiguration (bad migration SQL, an incompatible PostGIS install) is not
/// transient and should still fail loudly. This type encodes that distinction without taking a
/// dependency on a specific ADO.NET provider, so it lives in <c>Honua.Core</c> and is unit-testable.
/// </remarks>
public static class StartupDatabaseResilience
{
    /// <summary>
    /// PostgreSQL SQLSTATE codes (and code classes) that indicate a transient connectivity
    /// problem rather than a permanent configuration error.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><c>53300</c> — too_many_connections (pool/slot exhaustion).</description></item>
    /// <item><description><c>53400</c> — configuration_limit_exceeded.</description></item>
    /// <item><description><c>57P03</c> — cannot_connect_now (server still starting up).</description></item>
    /// <item><description><c>08*</c> — connection exception class (connection_failure, etc.).</description></item>
    /// </list>
    /// </remarks>
    private static readonly string[] TransientSqlStates =
    [
        "53300",
        "53400",
        "57P03",
    ];

    /// <summary>
    /// Determines whether the supplied exception (or any exception in its inner chain) represents a
    /// transient database connectivity failure that should not crash a degraded-start process.
    /// </summary>
    /// <param name="exception">The exception thrown while touching the database during startup.</param>
    /// <returns><see langword="true"/> when the failure looks transient (connectivity/availability);
    /// <see langword="false"/> for permanent failures such as bad migration SQL or schema errors.</returns>
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
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (!IsTransientConnectivityError(inner))
                {
                    return false;
                }
            }

            return true;
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

    /// <summary>
    /// Determines whether a PostgreSQL SQLSTATE code indicates a transient connectivity failure.
    /// </summary>
    /// <param name="sqlState">The five-character SQLSTATE code.</param>
    /// <returns><see langword="true"/> when the code is in the connection-exception class or a
    /// known transient resource-limit code.</returns>
    public static bool IsTransientSqlState(string? sqlState)
    {
        if (string.IsNullOrEmpty(sqlState))
        {
            return false;
        }

        // Class 08 — Connection Exception (connection_failure, sqlclient_unable_to_establish, etc.).
        if (sqlState.StartsWith("08", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var code in TransientSqlStates)
        {
            if (string.Equals(sqlState, code, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? TryReadSqlState(Exception exception)
    {
        // PostgresException exposes a public string SqlState property. Reading it via the runtime
        // property avoids a compile-time dependency on Npgsql from Core. Reflection here runs only
        // on the cold startup path (never in request hot paths) and is guarded against trimming by
        // the fact that the failing type is already materialized in the exception instance.
        var property = exception.GetType().GetProperty("SqlState");
        return property?.GetValue(exception) as string;
    }

    /// <summary>
    /// Computes the delay before the next degraded-start retry using bounded exponential backoff
    /// with full jitter.
    /// </summary>
    /// <param name="attempt">The 1-based retry attempt number.</param>
    /// <param name="baseDelay">The base delay for the first retry.</param>
    /// <param name="maxDelay">The ceiling for any single retry delay.</param>
    /// <param name="jitter">An optional source of [0,1) jitter; defaults to no jitter (deterministic
    /// for tests). Production callers pass <see cref="Random.Shared"/>.</param>
    /// <returns>The delay to wait before the next attempt.</returns>
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

    /// <summary>
    /// Builds the loud, operator-facing warning emitted when startup enters degraded mode so a real
    /// misconfiguration is never silently masked.
    /// </summary>
    /// <param name="phase">The startup phase that failed (e.g. "migrations", "PostGIS preflight").</param>
    /// <param name="errorMessage">The sanitized failure message.</param>
    /// <returns>A human-readable degraded-start warning.</returns>
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
