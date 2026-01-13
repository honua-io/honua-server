// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Honua.Postgres.Features.Infrastructure;

internal static class PostgresDataSourceFactory
{
    private const int DefaultMinPoolSize = 5;
    private const int DefaultConnectionIdleLifetimeSeconds = 300;
    private const int DefaultConnectionPruningIntervalSeconds = 10;
    private const int DefaultCommandTimeoutSeconds = 30;
    private const int DefaultBufferSizeBytes = 16384;

    public static NpgsqlDataSource Create(string connectionString, IConfiguration configuration, bool schemaHeadersEnabled)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var limits = ResolveConnectionLimits(configuration);
        return Create(connectionString, schemaHeadersEnabled, limits);
    }

    public static ConnectionLimits ResolveConnectionLimits(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var limits = new LimitsOptions();
        configuration.GetSection(LimitsOptions.SectionName).Bind(limits);
        return limits.Connections;
    }

    public static NpgsqlDataSource Create(string connectionString, bool schemaHeadersEnabled, ConnectionLimits limits)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        Configure(builder, schemaHeadersEnabled, limits);
        return builder.Build();
    }

    public static void Configure(NpgsqlDataSourceBuilder builder, bool schemaHeadersEnabled, ConnectionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(limits);

        var connectionStringBuilder = builder.ConnectionStringBuilder;

        connectionStringBuilder.Pooling = true;

        if (limits.MaxConnectionPoolSize > 0)
        {
            connectionStringBuilder.MaxPoolSize = limits.MaxConnectionPoolSize;
        }

        connectionStringBuilder.MinPoolSize = Math.Min(DefaultMinPoolSize, connectionStringBuilder.MaxPoolSize);
        connectionStringBuilder.ConnectionIdleLifetime = DefaultConnectionIdleLifetimeSeconds;
        connectionStringBuilder.ConnectionPruningInterval = DefaultConnectionPruningIntervalSeconds;
        connectionStringBuilder.CommandTimeout = DefaultCommandTimeoutSeconds;
        connectionStringBuilder.WriteBufferSize = DefaultBufferSizeBytes;
        connectionStringBuilder.ReadBufferSize = DefaultBufferSizeBytes;
        connectionStringBuilder.NoResetOnClose = !schemaHeadersEnabled;
        connectionStringBuilder.Multiplexing = !schemaHeadersEnabled;

        if (connectionStringBuilder.Multiplexing)
        {
            // Npgsql multiplexing does not support keepalive settings.
            connectionStringBuilder.KeepAlive = 0;
            connectionStringBuilder.TcpKeepAliveTime = 0;
            connectionStringBuilder.TcpKeepAliveInterval = 0;
        }
        else
        {
            connectionStringBuilder.KeepAlive = 30;
            connectionStringBuilder.TcpKeepAliveTime = 30;
            connectionStringBuilder.TcpKeepAliveInterval = 2;
        }

        // SECURITY: Configure lock timeouts to prevent indefinite blocking
        connectionStringBuilder.Options =
            "-c lock_timeout=30s -c statement_timeout=120s -c idle_in_transaction_session_timeout=60s";
    }
}
