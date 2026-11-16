// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Npgsql;

namespace Honua.Db.Postgres.Features.Infrastructure;

internal sealed class PostgresSessionLockConnections
{
    internal static readonly PostgresSessionLockConnections Pooled = new(releaseSessionOnClose: false);

    internal PostgresSessionLockConnections(bool releaseSessionOnClose, string? sessionSettingsSql = null)
    {
        ReleaseSessionOnClose = releaseSessionOnClose;
        SessionSettingsSql = sessionSettingsSql;
    }

    internal bool ReleaseSessionOnClose { get; }

    internal string? SessionSettingsSql { get; }

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

    internal bool RequiresUnpooled(string connectionString)
        => ReleaseSessionOnClose && new NpgsqlConnectionStringBuilder(connectionString).Pooling;

    internal string Resolve(string connectionString)
        => RequiresUnpooled(connectionString) ? BuildUnpooledConnectionString(connectionString) : connectionString;

    internal async Task<NpgsqlConnection> OpenAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        if (!RequiresUnpooled(dataSource.ConnectionString))
        {
            return await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return await OpenUnpooledAsync(dataSource.ConnectionString, cancellationToken).ConfigureAwait(false);
    }

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

    internal sealed class SessionConnection : IAsyncDisposable
    {
        internal SessionConnection(NpgsqlConnection connection, bool owned)
        {
            Connection = connection;
            IsUnpooledReplacement = owned;
        }

        internal NpgsqlConnection Connection { get; }

        internal bool IsUnpooledReplacement { get; }

        public static implicit operator NpgsqlConnection(SessionConnection session) => session.Connection;

        public ValueTask DisposeAsync()
            => IsUnpooledReplacement ? Connection.DisposeAsync() : ValueTask.CompletedTask;
    }
}
