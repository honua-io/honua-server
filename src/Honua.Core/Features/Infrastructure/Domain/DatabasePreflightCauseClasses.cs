// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>
/// Stable cause classes for a failed database startup preflight (honua-server#5916). The startup log
/// line and the readiness detail both carry the class, so an operator can tell a connection that
/// could not be obtained from a database that is genuinely incompatible. Only
/// <see cref="IncompatibleDatabase"/> is fatal; every other class starts the host degraded when
/// degraded start is enabled.
/// </summary>
public static class DatabasePreflightCauseClasses
{
    /// <summary>
    /// A connection could not be borrowed in time: the Npgsql pool was exhausted, or the RDS Proxy
    /// borrow queue timed out ("Timed-out waiting to acquire database connection").
    /// </summary>
    public const string ConnectionBorrowTimeout = "connection-borrow-timeout";

    /// <summary>The database endpoint refused or could not be reached (socket refused, host unreachable, SQLSTATE 08001/08004).</summary>
    public const string ConnectionRefused = "connection-refused";

    /// <summary>The database rejected the credentials (SQLSTATE class 28).</summary>
    public const string Auth = "auth";

    /// <summary>
    /// Any other availability failure: no free connection slots (SQLSTATE 53300), the server is
    /// starting (57P03), a connect timeout, or a broken connection.
    /// </summary>
    public const string ConnectionUnavailable = "connection-unavailable";

    /// <summary>
    /// The database answered but is not usable by this build: missing or mismatched PostGIS, a
    /// missing object, or a permanent TLS/protocol failure. Fatal at startup.
    /// </summary>
    public const string IncompatibleDatabase = "incompatible-database";

    /// <summary>Whether a cause class degrades startup (every class except <see cref="IncompatibleDatabase"/>).</summary>
    /// <param name="causeClass">The cause class.</param>
    /// <returns><see langword="true"/> when the host may start degraded.</returns>
    public static bool IsDegradable(string? causeClass)
        => causeClass is ConnectionBorrowTimeout or ConnectionRefused or Auth or ConnectionUnavailable;
}
