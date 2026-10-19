// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Sockets;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Resilience;
using Npgsql;

namespace Honua.Db.Postgres.Features.Infrastructure;

/// <summary>
/// Separates a database that is momentarily unreachable from one that is incompatible when the
/// startup compatibility probe fails. Transient: Npgsql's own <see cref="NpgsqlException.IsTransient"/>
/// flag, SQLSTATE classes <c>08</c> (connection exception) and <c>53</c> (insufficient resources,
/// including <c>53300</c> too_many_connections), <c>57P03</c> cannot_connect_now, timeouts and socket
/// failures. Everything else (authentication failures, missing objects, a wrong PostGIS install,
/// a permanent TLS or protocol failure) is not transient and keeps failing startup.
/// </summary>
internal static class PostgresPreflightFailureClassifier
{
    /// <summary>Whether <paramref name="exception"/> is a transient availability failure.</summary>
    /// <param name="exception">The failure raised by the compatibility probe.</param>
    /// <returns><see langword="true"/> when startup should treat the database as unavailable rather than incompatible.</returns>
    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
        {
            // One permanent failure makes the group fatal.
            return aggregate.InnerExceptions.All(IsTransient);
        }

        // A server-reported SQLSTATE is authoritative. Npgsql treats a wider set (deadlock,
        // admin shutdown) as transient; preflight keeps only classes 08 and 53, plus 57P03.
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return StartupDatabaseResilience.IsTransientSqlState(postgres.SqlState);
            }
        }

        // No SQLSTATE. Trust NpgsqlException.IsTransient, which is true only for a timeout,
        // a socket failure, or an IO failure. A permanent TLS, certificate, or protocol
        // failure reports IsTransient == false. Do not ask the Core helper first: it treats
        // every bare NpgsqlException as a connectivity error and would hide that flag.
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException npgsql)
            {
                return npgsql.IsTransient;
            }

            if (current is TimeoutException or SocketException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Names the cause class of a preflight failure (honua-server#5916): see
    /// <see cref="DatabasePreflightCauseClasses"/>. A connection that could not be borrowed (Npgsql
    /// pool exhaustion, an RDS Proxy borrow timeout) is <c>connection-borrow-timeout</c> whatever
    /// SQLSTATE the proxy attached; SQLSTATE class 28 is <c>auth</c>; a refused or unreachable
    /// endpoint is <c>connection-refused</c>; any other availability failure is
    /// <c>connection-unavailable</c>; everything else is <c>incompatible-database</c>, the only
    /// fatal class.
    /// </summary>
    /// <param name="exception">The failure raised by the compatibility probe.</param>
    /// <returns>The cause class.</returns>
    public static string Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
        {
            // One fatal failure makes the group fatal.
            var classes = aggregate.InnerExceptions.Select(Classify).ToArray();
            return classes.Contains(DatabasePreflightCauseClasses.IncompatibleDatabase, StringComparer.Ordinal)
                ? DatabasePreflightCauseClasses.IncompatibleDatabase
                : classes[0];
        }

        if (IsBorrowTimeout(exception))
        {
            return DatabasePreflightCauseClasses.ConnectionBorrowTimeout;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                var sqlState = postgres.SqlState ?? string.Empty;
                if (sqlState.StartsWith("28", StringComparison.Ordinal))
                {
                    return DatabasePreflightCauseClasses.Auth;
                }

                if (sqlState is "08001" or "08004")
                {
                    return DatabasePreflightCauseClasses.ConnectionRefused;
                }

                return StartupDatabaseResilience.IsTransientSqlState(sqlState)
                    ? DatabasePreflightCauseClasses.ConnectionUnavailable
                    : DatabasePreflightCauseClasses.IncompatibleDatabase;
            }
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket && IsRefusedOrUnreachable(socket.SocketErrorCode))
            {
                return DatabasePreflightCauseClasses.ConnectionRefused;
            }
        }

        return IsTransient(exception)
            ? DatabasePreflightCauseClasses.ConnectionUnavailable
            : DatabasePreflightCauseClasses.IncompatibleDatabase;
    }

    /// <summary>
    /// Whether the failure means a connection could not be obtained in time from the client pool or
    /// the RDS Proxy borrow queue. Matched on the provider message, which is never echoed.
    /// </summary>
    private static bool IsBorrowTimeout(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (ContainsBorrowMarker(current.Message)
                || (current is PostgresException postgres && ContainsBorrowMarker(postgres.MessageText)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsBorrowMarker(string? message)
        => !string.IsNullOrEmpty(message)
           && (message.Contains("connection pool has been exhausted", StringComparison.OrdinalIgnoreCase)
               || message.Contains("waiting to acquire database connection", StringComparison.OrdinalIgnoreCase)
               || message.Contains("borrow timeout", StringComparison.OrdinalIgnoreCase));

    private static bool IsRefusedOrUnreachable(SocketError error)
        => error is SocketError.ConnectionRefused
            or SocketError.HostNotFound
            or SocketError.HostUnreachable
            or SocketError.HostDown
            or SocketError.NetworkUnreachable
            or SocketError.NetworkDown
            or SocketError.TryAgain
            or SocketError.NoData;

    /// <summary>Sanitized failure class for health and log surfaces.</summary>
    /// <param name="exception">The failure raised by the compatibility probe.</param>
    /// <returns>A description that never echoes connection-string fragments.</returns>
    public static string Describe(Exception exception)
        => StartupDatabaseResilience.DescribeFailureClass(exception);
}
