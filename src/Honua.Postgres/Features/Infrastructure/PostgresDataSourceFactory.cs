// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Honua.Postgres.Features.Infrastructure;

internal static class PostgresDataSourceFactory
{
    public static NpgsqlDataSource Create(string connectionString, IConfiguration configuration, bool schemaHeadersEnabled)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var limits = ResolveConnectionLimits(configuration);
        var defaultSchema = configuration["Database:Schema"];
        return Create(connectionString, schemaHeadersEnabled, limits, defaultSchema);
    }

    public static ConnectionLimits ResolveConnectionLimits(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var limits = new ConnectionLimits();
        var section = configuration.GetSection($"{LimitsOptions.SectionName}:Connections");

        limits.MaxConcurrentQueries = ReadInt(section, nameof(ConnectionLimits.MaxConcurrentQueries), limits.MaxConcurrentQueries);
        limits.MaxConnectionPoolSize = ReadInt(section, nameof(ConnectionLimits.MaxConnectionPoolSize), limits.MaxConnectionPoolSize);
        limits.MinConnectionPoolSize = ReadInt(section, nameof(ConnectionLimits.MinConnectionPoolSize), limits.MinConnectionPoolSize);
        limits.ConnectionIdleLifetimeSeconds = ReadInt(section, nameof(ConnectionLimits.ConnectionIdleLifetimeSeconds), limits.ConnectionIdleLifetimeSeconds);
        limits.ConnectionPruningIntervalSeconds = ReadInt(section, nameof(ConnectionLimits.ConnectionPruningIntervalSeconds), limits.ConnectionPruningIntervalSeconds);
        limits.CommandTimeoutSeconds = ReadInt(section, nameof(ConnectionLimits.CommandTimeoutSeconds), limits.CommandTimeoutSeconds);
        limits.BufferSizeBytes = ReadInt(section, nameof(ConnectionLimits.BufferSizeBytes), limits.BufferSizeBytes);
        limits.RequestTimeout = ReadTimeSpan(section, nameof(ConnectionLimits.RequestTimeout), limits.RequestTimeout);
        limits.Multiplexing = section[nameof(ConnectionLimits.Multiplexing)] ?? limits.Multiplexing;
        limits.ConnectionAcquisitionTimeoutSeconds = ReadInt(section, nameof(ConnectionLimits.ConnectionAcquisitionTimeoutSeconds), limits.ConnectionAcquisitionTimeoutSeconds);
        limits.LockTimeout = ReadTimeSpan(section, nameof(ConnectionLimits.LockTimeout), limits.LockTimeout);
        limits.StatementTimeout = ReadTimeSpan(section, nameof(ConnectionLimits.StatementTimeout), limits.StatementTimeout);
        limits.IdleInTransactionTimeout = ReadTimeSpan(section, nameof(ConnectionLimits.IdleInTransactionTimeout), limits.IdleInTransactionTimeout);

        return limits;
    }

    public static NpgsqlDataSource Create(string connectionString, bool schemaHeadersEnabled, ConnectionLimits limits, string? defaultSchema = null)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        Configure(builder, schemaHeadersEnabled, limits, defaultSchema);
        return builder.Build();
    }

    public static void Configure(NpgsqlDataSourceBuilder builder, bool schemaHeadersEnabled, ConnectionLimits limits, string? defaultSchema = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(limits);

        var connectionStringBuilder = builder.ConnectionStringBuilder;

        connectionStringBuilder.Pooling = true;

        if (limits.MaxConnectionPoolSize > 0)
        {
            connectionStringBuilder.MaxPoolSize = limits.MaxConnectionPoolSize;
        }

        connectionStringBuilder.MinPoolSize = Math.Min(limits.MinConnectionPoolSize, connectionStringBuilder.MaxPoolSize);
        connectionStringBuilder.ConnectionIdleLifetime = limits.ConnectionIdleLifetimeSeconds;
        connectionStringBuilder.ConnectionPruningInterval = limits.ConnectionPruningIntervalSeconds;
        connectionStringBuilder.CommandTimeout = limits.CommandTimeoutSeconds;
        connectionStringBuilder.WriteBufferSize = limits.BufferSizeBytes;
        connectionStringBuilder.ReadBufferSize = limits.BufferSizeBytes;
        // Schema headers always force multiplexing off (SET search_path is
        // per-physical-connection and unsafe with multiplexing).
        var useMultiplexing = ResolveMultiplexing(limits.Multiplexing, schemaHeadersEnabled);
        connectionStringBuilder.Multiplexing = useMultiplexing;
        // Keep NoResetOnClose=true when schema headers are off so that RESET ALL
        // is NOT sent on pool return — otherwise Npgsql clears the search_path set
        // via the Options parameter, breaking schema-qualified queries on reused
        // connections.  When schema headers are enabled (test isolation), we need
        // the reset so per-test SET search_path overrides are cleared.
        connectionStringBuilder.NoResetOnClose = !schemaHeadersEnabled;

        if (useMultiplexing)
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
        var lockTimeout = (int)limits.LockTimeout.TotalSeconds;
        var statementTimeout = (int)limits.StatementTimeout.TotalSeconds;
        var idleTimeout = (int)limits.IdleInTransactionTimeout.TotalSeconds;
        var options =
            $"-c lock_timeout={lockTimeout}s -c statement_timeout={statementTimeout}s -c idle_in_transaction_session_timeout={idleTimeout}s";

        // When a default schema is known and schema headers are NOT active, embed
        // search_path in the connection string. This avoids a per-connection SET
        // round-trip and is safe with multiplexing (Options apply per-session, not
        // per-physical-connection).
        if (!schemaHeadersEnabled &&
            !string.IsNullOrWhiteSpace(defaultSchema) &&
            SchemaSearchPath.IsValidIdentifier(defaultSchema))
        {
            options += $" -c search_path=\"{defaultSchema.Trim()}\",public";
        }

        connectionStringBuilder.Options = options;
    }

    /// <summary>
    /// Resolves the effective multiplexing setting.
    /// Schema headers always force multiplexing off regardless of config.
    /// Accepted values are <c>"auto"</c>, <c>"true"</c>, and <c>"false"</c>
    /// (case-insensitive). Null, empty, and unrecognized values fall back to
    /// the documented default (<c>"false"</c>) so a typo cannot silently
    /// enable multiplexing; the configuration validation system
    /// rejects unrecognized values at startup as a paired fail-fast guard.
    /// </summary>
    internal static bool ResolveMultiplexing(string? multiplexingSetting, bool schemaHeadersEnabled)
    {
        if (schemaHeadersEnabled)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(multiplexingSetting))
        {
            return false;
        }

        if (string.Equals(multiplexingSetting, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(multiplexingSetting, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Includes "false" and any unrecognized token — default to the safe off
        // behavior so typos cannot silently flip the runtime contract.
        return false;
    }

    private static int ReadInt(IConfiguration section, string key, int fallback)
        => int.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static TimeSpan ReadTimeSpan(IConfiguration section, string key, TimeSpan fallback)
        => TimeSpan.TryParse(section[key], CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
