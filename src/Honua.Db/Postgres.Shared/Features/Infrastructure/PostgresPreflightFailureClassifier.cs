// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Resilience;
using Npgsql;

namespace Honua.Db.Postgres.Features.Infrastructure;

/// <summary>
/// Separates a database that is momentarily unreachable from one that is incompatible when the
/// startup compatibility probe fails. Transient: Npgsql's own <see cref="NpgsqlException.IsTransient"/>
/// flag, SQLSTATE classes <c>08</c> (connection exception) and <c>53</c> (insufficient resources,
/// including <c>53300</c> too_many_connections), <c>57P03</c> cannot_connect_now, timeouts and socket
/// failures. Everything else (authentication failures, missing objects, a wrong PostGIS install) is
/// not transient and keeps failing startup.
/// </summary>
internal static class PostgresPreflightFailureClassifier
{
    /// <summary>Whether <paramref name="exception"/> is a transient availability failure.</summary>
    /// <param name="exception">The failure raised by the compatibility probe.</param>
    /// <returns><see langword="true"/> when startup should treat the database as unavailable rather than incompatible.</returns>
    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (StartupDatabaseResilience.IsTransientConnectivityError(exception))
        {
            return true;
        }

        if (exception is AggregateException)
        {
            // The Core classifier already required every leaf to be transient.
            return false;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                // A server-reported SQLSTATE is authoritative: only the classes above are transient.
                return StartupDatabaseResilience.IsTransientSqlState(postgres.SqlState);
            }

            if (current is NpgsqlException { IsTransient: true })
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
