// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Sockets;
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

    /// <summary>Sanitized failure class for health and log surfaces.</summary>
    /// <param name="exception">The failure raised by the compatibility probe.</param>
    /// <returns>A description that never echoes connection-string fragments.</returns>
    public static string Describe(Exception exception)
        => StartupDatabaseResilience.DescribeFailureClass(exception);
}
