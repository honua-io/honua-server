// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Db.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Honua.Db.Postgres.Features.Security;

internal sealed class SecureConnectionDataSourceCache : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, CacheEntry> _dataSources = new(StringComparer.Ordinal);
    private readonly bool _schemaHeadersEnabled;
    private readonly ConnectionLimits _connectionLimits;
    private readonly string? _defaultSchema;
    private readonly Func<string, NpgsqlDataSource> _createDataSource;
    private bool _disposed;

    public SecureConnectionDataSourceCache(IConfiguration configuration)
        : this(configuration, null)
    {
    }

    internal SecureConnectionDataSourceCache(
        IConfiguration configuration,
        Func<string, NpgsqlDataSource>? createDataSource)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Request-scoped schema mode covers per-test schema headers and tenant schema routing (#346).
        _schemaHeadersEnabled = RequestScopedSchemaConfiguration.IsEnabled(configuration);
        _connectionLimits = PostgresDataSourceFactory.ResolveConnectionLimits(configuration);
        // Preserve the configured default schema so named secure connections get the
        // same search_path wiring as the default data source built in
        // ServiceCollectionExtensions.AddPostgreSqlServices — both call
        // PostgresDataSourceFactory.Create with this value, so the schema is applied via
        // the same RDS-Proxy-safe mechanism (physical-connection initializer SET, or the
        // libpq `options` startup parameter under multiplexing/schema-header modes —
        // honua-server#1638). Without this, background/service callers (where
        // ISchemaContext.CurrentSchema is null) fall back to the PostgreSQL default
        // search_path and miss schema-qualified tables (honua-server#2949).
        _defaultSchema = configuration["Database:Schema"];
        _createDataSource = createDataSource ?? (connectionString =>
            PostgresDataSourceFactory.Create(connectionString, _schemaHeadersEnabled, _connectionLimits, _defaultSchema));
    }

    public NpgsqlDataSource GetOrCreate(string connectionString)
        => GetOrCreate(connectionString, connectionString);

    /// <summary>
    /// Gets (or creates) the pooled data source for a logical connection. When the
    /// resolved connection string for that logical connection changes — e.g. the
    /// secrets-manager password rotated — the stale data source is disposed so its
    /// pooled connections are released instead of accumulating per rotation and
    /// eventually exhausting server-side connection slots.
    /// </summary>
    public NpgsqlDataSource GetOrCreate(string connectionName, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionName))
        {
            throw new ArgumentException("Connection name cannot be null or empty.", nameof(connectionName));
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _dataSources.TryGetValue(connectionName, out var entry);
            if (entry != null && string.Equals(entry.ConnectionString, connectionString, StringComparison.Ordinal))
            {
                return entry.DataSource;
            }

            // Construction opens no database connection. Keep it under the lifecycle
            // lock so rotation or shutdown cannot abandon an initializing pool. Build
            // before replacing the entry so a failed rotation preserves the old pool.
            var dataSource = _createDataSource(connectionString);
            _dataSources[connectionName] = new CacheEntry(connectionString, dataSource);

            // Npgsql closes idle pooled connections immediately and rented ones as
            // they are returned, allowing commands already using the old pool to finish.
            entry?.DataSource.Dispose();
            return dataSource;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var entry in _dataSources.Values)
            {
                entry.DataSource.Dispose();
            }

            _dataSources.Clear();
        }
    }

    private sealed record CacheEntry(string ConnectionString, NpgsqlDataSource DataSource);
}
