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
        limits.AdaptiveConcurrencyEnabled = ReadBool(section, nameof(ConnectionLimits.AdaptiveConcurrencyEnabled), limits.AdaptiveConcurrencyEnabled);
        limits.AdaptiveConcurrencyMinQueries = ReadInt(section, nameof(ConnectionLimits.AdaptiveConcurrencyMinQueries), limits.AdaptiveConcurrencyMinQueries);
        limits.AdaptiveConcurrencyInitialQueries = ReadInt(section, nameof(ConnectionLimits.AdaptiveConcurrencyInitialQueries), limits.AdaptiveConcurrencyInitialQueries);
        limits.AdaptiveConcurrencyMaxQueries = ReadInt(section, nameof(ConnectionLimits.AdaptiveConcurrencyMaxQueries), limits.AdaptiveConcurrencyMaxQueries);
        limits.AdaptiveConcurrencyTargetDurationMs = ReadInt(section, nameof(ConnectionLimits.AdaptiveConcurrencyTargetDurationMs), limits.AdaptiveConcurrencyTargetDurationMs);
        limits.AdaptiveConcurrencyUpdateIntervalMs = ReadInt(section, nameof(ConnectionLimits.AdaptiveConcurrencyUpdateIntervalMs), limits.AdaptiveConcurrencyUpdateIntervalMs);
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
        // is NOT sent on pool return — otherwise Npgsql clears the session settings
        // (timeouts + search_path) applied by the physical-connection initializer
        // below, breaking schema-qualified queries on reused connections.  When
        // schema headers are enabled (test isolation), we need the reset so
        // per-test SET search_path overrides are cleared.
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

        // SECURITY: server-side timeouts (lock/statement/idle-in-transaction) prevent
        // indefinite blocking, and an optional default search_path avoids a
        // per-request SET round-trip. How these session settings reach the server
        // matters (issue #1638):
        //
        // - Default path (no multiplexing, no schema headers): apply them via a
        //   physical-connection initializer that runs plain SET statements right
        //   after each physical connection opens. Combined with NoResetOnClose=true
        //   (no RESET ALL on pool return, see above) the settings last for the
        //   lifetime of the physical connection — identical semantics to the
        //   previous startup-options approach. Crucially this avoids the libpq
        //   `options` STARTUP parameter, which AWS RDS Proxy rejects outright
        //   ("0A000: Feature not supported: RDS Proxy currently doesn't support
        //   command-line options") and which transaction-mode poolers (pgbouncer)
        //   break on. Behind RDS Proxy the per-connection SET causes session
        //   pinning — acceptable: connection-slot protection is the reason a proxy
        //   is deployed in the first place.
        //
        // - Multiplexing: keep the `options` startup parameter. Startup parameters
        //   become per-session defaults that survive RESET ALL, which is the only
        //   reliable way to guarantee these settings when commands from many logical
        //   sessions interleave over shared physical connections. Consequence:
        //   Limits:Connections:Multiplexing=true is mutually exclusive with
        //   RDS Proxy / transaction-mode poolers (documented in
        //   docs/reference/configuration/data-sources/postgis.md). Multiplexing
        //   already defaults off (see ResolveMultiplexing).
        //
        // - Schema headers (test isolation): keep the `options` startup parameter.
        //   NoResetOnClose=false in this mode, so Npgsql resets session state on
        //   every pool return; initializer-applied SETs would be wiped by the first
        //   reset, while startup options survive RESET ALL as session defaults. This
        //   mode is test-only and never runs behind a connection proxy.
        var searchPathSchema = ResolveSearchPathSchema(schemaHeadersEnabled, defaultSchema);

        if (useMultiplexing || schemaHeadersEnabled)
        {
            connectionStringBuilder.Options = BuildSessionSettingsStartupOptions(limits, searchPathSchema);
        }
        else
        {
            var initializerSql = BuildSessionSettingsSql(limits, searchPathSchema);
            builder.UsePhysicalConnectionInitializer(
                connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = initializerSql;
                    command.ExecuteNonQuery();
                },
                async connection =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = initializerSql;
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                });
        }
    }

    /// <summary>
    /// Resolves the schema embedded in the default search_path, or <see langword="null"/>
    /// when none applies. Schema-header mode owns search_path per request, and only
    /// values that pass the <see cref="SchemaSearchPath.IsValidIdentifier"/> allow-list
    /// are accepted (SQL-injection guard — the value is later interpolated as a quoted
    /// identifier).
    /// </summary>
    internal static string? ResolveSearchPathSchema(bool schemaHeadersEnabled, string? defaultSchema)
    {
        if (!schemaHeadersEnabled &&
            !string.IsNullOrWhiteSpace(defaultSchema) &&
            SchemaSearchPath.IsValidIdentifier(defaultSchema))
        {
            return defaultSchema.Trim();
        }

        return null;
    }

    /// <summary>
    /// Builds the libpq <c>options</c> startup parameter carrying the session settings.
    /// Used only for multiplexing and schema-header modes — RDS Proxy and
    /// transaction-mode poolers reject this startup parameter, so the default path uses
    /// <see cref="BuildSessionSettingsSql"/> instead.
    /// </summary>
    internal static string BuildSessionSettingsStartupOptions(ConnectionLimits limits, string? searchPathSchema)
    {
        ArgumentNullException.ThrowIfNull(limits);

        var (lockTimeout, statementTimeout, idleTimeout) = GetTimeoutSeconds(limits);
        var options =
            $"-c lock_timeout={lockTimeout}s -c statement_timeout={statementTimeout}s -c idle_in_transaction_session_timeout={idleTimeout}s";

        if (searchPathSchema is not null)
        {
            options += $" -c search_path=\"{searchPathSchema}\",public";
        }

        return options;
    }

    /// <summary>
    /// Builds the <c>SET</c> batch executed by the physical-connection initializer on
    /// the default (non-multiplexing) path. Plain SET statements after connect are
    /// compatible with AWS RDS Proxy and pgbouncer, unlike the <c>options</c> startup
    /// parameter (issue #1638).
    /// </summary>
    internal static string BuildSessionSettingsSql(ConnectionLimits limits, string? searchPathSchema)
    {
        ArgumentNullException.ThrowIfNull(limits);

        var (lockTimeout, statementTimeout, idleTimeout) = GetTimeoutSeconds(limits);
        // codeql[cs/sql-injection]: timeout values are integers derived from configuration
        // TimeSpans, and searchPathSchema is validated against the ^[A-Za-z_][A-Za-z0-9_]*$
        // allow-list in ResolveSearchPathSchema; PostgreSQL SET does not accept parameter
        // binding for these settings.
        var sql =
            $"SET lock_timeout = '{lockTimeout}s'; " +
            $"SET statement_timeout = '{statementTimeout}s'; " +
            $"SET idle_in_transaction_session_timeout = '{idleTimeout}s';";

        if (searchPathSchema is not null)
        {
            sql += $" SET search_path = \"{searchPathSchema}\", public;";
        }

        return sql;
    }

    private static (int LockTimeout, int StatementTimeout, int IdleTimeout) GetTimeoutSeconds(ConnectionLimits limits)
        => ((int)limits.LockTimeout.TotalSeconds,
            (int)limits.StatementTimeout.TotalSeconds,
            (int)limits.IdleInTransactionTimeout.TotalSeconds);

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

    private static bool ReadBool(IConfiguration section, string key, bool fallback)
        => bool.TryParse(section[key], out var value) ? value : fallback;

    private static TimeSpan ReadTimeSpan(IConfiguration section, string key, TimeSpan fallback)
        => TimeSpan.TryParse(section[key], CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
