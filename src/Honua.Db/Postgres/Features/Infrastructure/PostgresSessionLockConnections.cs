// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Npgsql;

namespace Honua.Db.Postgres.Features.Infrastructure;

/// <summary>
/// Chooses the connection for work that holds a session advisory lock (imports and layer
/// publish). Behind RDS Proxy a session advisory lock pins the proxy session, and unlocking
/// does not unpin it. A frozen Lambda never prunes its Npgsql pool, so a pooled connection
/// keeps its pinned proxy backend until the proxy's idle-client timeout (honua-server#6765).
/// </summary>
/// <remarks>
/// With <see cref="ReleaseSessionOnClose"/> (Lambda), a pooled connection is replaced by an
/// unpooled connection to the same database with the same credentials, so closing it ends the
/// session. The lock and the locked work share that one session: the importer and publisher
/// re-take the same key as a transaction lock on the work connection and rely on advisory-lock
/// reentrancy within a session, so the lock cannot move to a separate connection. Long-lived
/// hosts keep their pooled connections (<see cref="Pooled"/>).
/// Behind RDS Proxy the server runs <c>Database:SessionSettings:Mode=role-defaults</c>, so
/// the role's session timeouts apply to the unpooled connection as they do to the pool.
/// </remarks>
internal sealed class PostgresSessionLockConnections
{
    /// <summary>Long-lived hosts: session locks stay on pooled connections.</summary>
    internal static readonly PostgresSessionLockConnections Pooled = new(releaseSessionOnClose: false);

    internal PostgresSessionLockConnections(bool releaseSessionOnClose)
    {
        ReleaseSessionOnClose = releaseSessionOnClose;
    }

    /// <summary>Whether a session-lock connection must end its physical session when it closes.</summary>
    internal bool ReleaseSessionOnClose { get; }

    /// <summary>
    /// Whether a connection opened from <paramref name="connectionString"/> must be replaced.
    /// An already unpooled connection (for example a Lambda named or bound pool,
    /// honua-server#6760) ends its session on close.
    /// </summary>
    internal bool RequiresUnpooled(string connectionString)
        => ReleaseSessionOnClose && new NpgsqlConnectionStringBuilder(connectionString).Pooling;

    /// <summary>The connection string to open a session-lock connection from.</summary>
    internal string Resolve(string connectionString)
        => RequiresUnpooled(connectionString) ? BuildUnpooledConnectionString(connectionString) : connectionString;

    /// <summary>
    /// Opens the session-lock connection for <paramref name="dataSource"/>: from the pool on a
    /// long-lived host, unpooled on Lambda. The data source's connection string must carry the
    /// password; <see cref="PostgresDataSourceFactory"/> sets <c>Persist Security Info</c>.
    /// </summary>
    internal async Task<NpgsqlConnection> OpenAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        if (!RequiresUnpooled(dataSource.ConnectionString))
        {
            return await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return await OpenUnpooledAsync(dataSource.ConnectionString, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the connection to hold the lock and do the locked work on. On Lambda a pooled
    /// <paramref name="workConnection"/> is replaced by an unpooled connection built from its
    /// connection string; the caller keeps its lease on <paramref name="workConnection"/>
    /// (for example the query concurrency gate slot) until the work ends. A connection with a
    /// caller-owned transaction (<paramref name="hasCallerTransaction"/>) is never replaced,
    /// because the work must join that transaction.
    /// </summary>
    internal async Task<SessionConnection> OpenAsync(
        NpgsqlConnection workConnection,
        CancellationToken cancellationToken,
        bool hasCallerTransaction = false)
    {
        ArgumentNullException.ThrowIfNull(workConnection);
        if (hasCallerTransaction || !RequiresUnpooled(workConnection.ConnectionString))
        {
            return new SessionConnection(workConnection, owned: false);
        }

        var connection = await OpenUnpooledAsync(workConnection.ConnectionString, cancellationToken)
            .ConfigureAwait(false);
        return new SessionConnection(connection, owned: true);
    }

    /// <summary>The same database and credentials, unpooled.</summary>
    internal static string BuildUnpooledConnectionString(string connectionString)
        => new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Multiplexing = false,
            MinPoolSize = 0,
        }.ConnectionString;

    private static async Task<NpgsqlConnection> OpenUnpooledAsync(
        string connectionString, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(BuildUnpooledConnectionString(connectionString));
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return connection;
    }

    /// <summary>
    /// A session-lock connection. Disposing closes it only when it replaced the caller's work
    /// connection, which ends the session and any lock it still holds.
    /// </summary>
    internal sealed class SessionConnection : IAsyncDisposable
    {
        internal SessionConnection(NpgsqlConnection connection, bool owned)
        {
            Connection = connection;
            IsUnpooledReplacement = owned;
        }

        /// <summary>The connection to take the lock and run the locked work on.</summary>
        internal NpgsqlConnection Connection { get; }

        /// <summary>Whether this is an unpooled replacement rather than the caller's connection.</summary>
        internal bool IsUnpooledReplacement { get; }

        /// <summary>Implicit conversion for call sites that take an <see cref="NpgsqlConnection"/>.</summary>
        public static implicit operator NpgsqlConnection(SessionConnection session) => session.Connection;

        public ValueTask DisposeAsync()
            => IsUnpooledReplacement ? Connection.DisposeAsync() : ValueTask.CompletedTask;
    }
}
