// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
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
/// A replacement keeps the pool's session settings. Settings carried in the connection string
/// (startup options) and role defaults (<c>role-defaults</c> mode) apply on their own. In the
/// default <c>connection</c> mode the pool applies them with a physical-connection initializer,
/// which a bare connection skips, so the replacement runs <see cref="SessionSettingsSql"/>
/// after it opens. Only the primary pool can need a replacement: on Lambda the named and bound
/// pools that use the initializer are already unpooled (honua-server#6760).
/// </remarks>
internal sealed class PostgresSessionLockConnections
{
    /// <summary>Long-lived hosts: session locks stay on pooled connections.</summary>
    internal static readonly PostgresSessionLockConnections Pooled = new(releaseSessionOnClose: false);

    internal PostgresSessionLockConnections(bool releaseSessionOnClose, string? sessionSettingsSql = null)
    {
        ReleaseSessionOnClose = releaseSessionOnClose;
        SessionSettingsSql = sessionSettingsSql;
    }

    /// <summary>Whether a session-lock connection must end its physical session when it closes.</summary>
    internal bool ReleaseSessionOnClose { get; }

    /// <summary>
    /// The primary pool's per-connection session settings, or <see langword="null"/> when the
    /// pool applies none by initializer. Run on every unpooled replacement.
    /// </summary>
    internal string? SessionSettingsSql { get; }

    /// <summary>
    /// Builds the policy for the primary data source, mirroring how
    /// <see cref="PostgresDataSourceFactory.Configure"/> applies its session settings.
    /// </summary>
    internal static PostgresSessionLockConnections ForPrimaryDataSource(
        bool releaseSessionOnClose,
        ConnectionLimits limits,
        bool schemaHeadersEnabled,
        string? defaultSchema,
        DatabaseSessionSettingsMode sessionSettingsMode)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var usesInitializer = sessionSettingsMode != DatabaseSessionSettingsMode.RoleDefaults
            && !schemaHeadersEnabled
            && !PostgresDataSourceFactory.ResolveMultiplexing(limits.Multiplexing, schemaHeadersEnabled);
        var sql = usesInitializer
            ? PostgresDataSourceFactory.BuildSessionSettingsSql(
                limits, PostgresDataSourceFactory.ResolveSearchPathSchema(schemaHeadersEnabled, defaultSchema))
            : null;
        return new PostgresSessionLockConnections(releaseSessionOnClose, sql);
    }

    /// <summary>
    /// Whether a connection opened from <paramref name="connectionString"/> must be replaced.
    /// An already unpooled connection (for example a Lambda named or bound pool,
    /// honua-server#6760) ends its session on close.
    /// </summary>
    internal bool RequiresUnpooled(string connectionString)
        => ReleaseSessionOnClose && new NpgsqlConnectionStringBuilder(connectionString).Pooling;

    /// <summary>
    /// The connection string to open a session-lock connection from. For a caller that opens a
    /// bare connection from a raw string, which never had the pool's initializer.
    /// </summary>
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

    private async Task<NpgsqlConnection> OpenUnpooledAsync(
        string connectionString, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(BuildUnpooledConnectionString(connectionString));
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (SessionSettingsSql is { Length: > 0 } sessionSettingsSql)
            {
                await using var command = new NpgsqlCommand(sessionSettingsSql, connection);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
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
