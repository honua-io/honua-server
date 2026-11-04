// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Sockets;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Resilience;
using Npgsql;

namespace Honua.Db.Postgres.Features.Infrastructure;

internal static class PostgresPreflightFailureClassifier
{
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

    public static string Describe(Exception exception)
        => StartupDatabaseResilience.DescribeFailureClass(exception);
}
