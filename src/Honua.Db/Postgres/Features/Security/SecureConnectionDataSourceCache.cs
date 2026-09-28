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
    private readonly Dictionary<(string Name, bool PreservePrimarySchema), CacheEntry> _dataSources = new();
    private readonly bool _schemaHeadersEnabled;
    private readonly ConnectionLimits _connectionLimits;
    private readonly string? _defaultSchema;
    private readonly Func<string, NpgsqlDataSource>? _createDataSource;
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
        _createDataSource = createDataSource;
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
        ValidateArguments(connectionName, connectionString);
        lock (_sync)
        {
            return GetOrCreateEntry(connectionName, connectionString, preservePrimarySchema: true).DataSource;
        }
    }

    /// <summary>
    /// Pins a pool through its connection lifetime, including an in-flight open.
    /// Rotation and shutdown retire pinned pools; disposal waits for the last pin.
    /// Multiplexed logical connections need their source until the lease is returned.
    /// </summary>
    public Acquisition Acquire(string connectionName, string connectionString, bool preservePrimarySchema = true)
    {
        ValidateArguments(connectionName, connectionString);
        lock (_sync)
        {
            var entry = GetOrCreateEntry(connectionName, connectionString, preservePrimarySchema);
            entry.Acquisitions++;
            return new Acquisition(entry.DataSource, () => Release(entry));
        }
    }

    private static void ValidateArguments(string connectionName, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionName))
        {
            throw new ArgumentException("Connection name cannot be null or empty.", nameof(connectionName));
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));
        }
    }

    // Caller holds _sync across lookup, construction, replacement and acquisition.
    private CacheEntry GetOrCreateEntry(string connectionName, string connectionString, bool preservePrimarySchema)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = (connectionName, preservePrimarySchema);
        _dataSources.TryGetValue(key, out var entry);
        if (entry != null && string.Equals(entry.ConnectionString, connectionString, StringComparison.Ordinal))
        {
            return entry;
        }

        // Construction opens no database connection. Build before replacing the
        // entry so a failed rotation preserves the old pool. Source-bound pools use
        // common limits/timeouts without inheriting the primary database's schema.
        var dataSource = _createDataSource?.Invoke(connectionString) ?? (preservePrimarySchema
            ? PostgresDataSourceFactory.Create(connectionString, _schemaHeadersEnabled, _connectionLimits, _defaultSchema)
            : PostgresDataSourceFactory.CreateForBoundSource(connectionString, _connectionLimits));
        var replacement = new CacheEntry(connectionString, dataSource);
        _dataSources[key] = replacement;
        if (entry != null)
        {
            Retire(entry);
        }

        return replacement;
    }

    private void Release(CacheEntry entry)
    {
        lock (_sync)
        {
            entry.Acquisitions--;
            if (entry.Retired && entry.Acquisitions == 0)
            {
                entry.DataSource.Dispose();
            }
        }
    }

    private static void Retire(CacheEntry entry)
    {
        entry.Retired = true;
        if (entry.Acquisitions == 0)
        {
            entry.DataSource.Dispose();
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
                Retire(entry);
            }

            _dataSources.Clear();
        }
    }

    private sealed class CacheEntry(string connectionString, NpgsqlDataSource dataSource)
    {
        public string ConnectionString { get; } = connectionString;
        public NpgsqlDataSource DataSource { get; } = dataSource;
        public int Acquisitions { get; set; }
        public bool Retired { get; set; }
    }

    internal sealed class Acquisition(NpgsqlDataSource dataSource, Action release) : IDisposable
    {
        private Action? _release = release;

        public NpgsqlDataSource DataSource { get; } = dataSource;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
