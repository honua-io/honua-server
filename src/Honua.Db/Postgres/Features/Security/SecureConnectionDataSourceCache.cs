// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Db.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Db.Postgres.Features.Security;

internal sealed class SecureConnectionDataSourceCache : IDisposable, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<(string Name, bool PreservePrimarySchema), CacheEntry> _dataSources = new();
    private readonly bool _schemaHeadersEnabled;
    private readonly ConnectionLimits _connectionLimits;
    private readonly string? _defaultSchema;
    private readonly Func<string, NpgsqlDataSource>? _createDataSource;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleLifetime;
    private readonly ITimer _pruningTimer;
    private readonly ILogger<SecureConnectionDataSourceCache> _logger;
    private bool _disposed;

    private static readonly Action<ILogger, Exception?> _logRetiredPoolDisposalFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(1, nameof(_logRetiredPoolDisposalFailed)),
            "Failed to dispose a retired secure connection pool");

    public SecureConnectionDataSourceCache(
        IConfiguration configuration,
        TimeProvider? timeProvider = null,
        ILogger<SecureConnectionDataSourceCache>? logger = null)
        : this(configuration, createDataSource: null, timeProvider, logger)
    {
    }

    internal SecureConnectionDataSourceCache(
        IConfiguration configuration,
        Func<string, NpgsqlDataSource>? createDataSource,
        TimeProvider? timeProvider = null,
        ILogger<SecureConnectionDataSourceCache>? logger = null)
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
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<SecureConnectionDataSourceCache>.Instance;
        _idleLifetime = TimeSpan.FromSeconds(Math.Max(1, _connectionLimits.ConnectionIdleLifetimeSeconds));
        var interval = TimeSpan.FromSeconds(Math.Max(1, _connectionLimits.ConnectionPruningIntervalSeconds));
        _pruningTimer = CreatePruningTimer(interval);
    }

    private ITimer CreatePruningTimer(TimeSpan interval)
    {
        if (ExecutionContext.IsFlowSuppressed())
        {
            return _timeProvider.CreateTimer(static state =>
                ((SecureConnectionDataSourceCache)state!).PruneIdlePools(), this, interval, interval);
        }

        // A singleton may first resolve inside a request. Its maintenance timer must
        // not retain that request's AsyncLocals/ExecutionContext for the host lifetime.
        using (ExecutionContext.SuppressFlow())
        {
            return _timeProvider.CreateTimer(static state =>
                ((SecureConnectionDataSourceCache)state!).PruneIdlePools(), this, interval, interval);
        }
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
            entry.LastUsedTimestamp = _timeProvider.GetTimestamp();
            return entry;
        }

        // Construction opens no database connection. Build before replacing the
        // entry so a failed rotation preserves the old pool. Source-bound pools use
        // common limits/timeouts without inheriting the primary database's schema.
        var dataSource = _createDataSource?.Invoke(connectionString) ?? (preservePrimarySchema
            ? PostgresDataSourceFactory.Create(connectionString, _schemaHeadersEnabled, _connectionLimits, _defaultSchema)
            : PostgresDataSourceFactory.CreateForBoundSource(connectionString, _connectionLimits));
        var replacement = new CacheEntry(connectionString, dataSource, _timeProvider.GetTimestamp());
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
            entry.LastUsedTimestamp = _timeProvider.GetTimestamp();
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

    /// <summary>
    /// Retires only this registered connection's local named and bound pools.
    /// Existing pins survive; other instances and late opens are bounded by idle pruning.
    /// </summary>
    public void RetireConnection(Guid connectionId, string connectionName)
    {
        List<NpgsqlDataSource> retired = [];
        lock (_sync)
        {
            RemoveEntry((connectionName, true), retired);
            RemoveEntry(("bound-id:" + connectionId.ToString("D"), false), retired);
        }

        DisposeRetiredPools(retired);
    }

    private void RemoveEntry((string Name, bool PreservePrimarySchema) key, List<NpgsqlDataSource> retired)
    {
        if (_dataSources.Remove(key, out var entry))
        {
            entry.Retired = true;
            if (entry.Acquisitions == 0)
            {
                retired.Add(entry.DataSource);
            }
        }
    }

    private void PruneIdlePools()
    {
        List<NpgsqlDataSource> retired = [];
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var now = _timeProvider.GetTimestamp();
            List<(string Name, bool PreservePrimarySchema)>? expired = null;
            foreach (var (key, entry) in _dataSources)
            {
                // Pins cover ongoing opens and the full logical connection lifetime.
                // Idle time starts when the last lease returns, not when it opened.
                if (entry.Acquisitions == 0 &&
                    _timeProvider.GetElapsedTime(entry.LastUsedTimestamp, now) >= _idleLifetime)
                {
                    (expired ??= []).Add(key);
                }
            }

            if (expired is not null)
            {
                foreach (var key in expired)
                {
                    RemoveEntry(key, retired);
                }
            }
        }

        // A sweep can retire many pools. Close them outside the shared lookup lock
        // so unrelated sources can continue acquiring connections during cleanup.
        DisposeRetiredPools(retired);
    }

    private void DisposeRetiredPools(List<NpgsqlDataSource> retired)
    {
        foreach (var source in retired)
        {
            try
            {
                source.Dispose();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Timer callbacks must not terminate the host, and one failed close
                // must not prevent the rest of a retired batch from being released.
                _logRetiredPoolDisposalFailed(_logger, exception);
            }
        }
    }

    /// <summary>
    /// Stops future maintenance and retires cached entries. An already-running sweep
    /// retains ownership of its detached batch; asynchronous host disposal waits for it.
    /// </summary>
    public void Dispose()
    {
        var retired = DetachForDisposal();
        if (retired is null)
        {
            return;
        }

        try
        {
            _pruningTimer.Dispose();
        }
        finally
        {
            DisposeRetiredPools(retired);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var retired = DetachForDisposal();
        try
        {
            // No lookup lock is held while waiting for an active maintenance callback.
            await _pruningTimer.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (retired is not null)
            {
                DisposeRetiredPools(retired);
            }
        }
    }

    private List<NpgsqlDataSource>? DetachForDisposal()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return null;
            }

            _disposed = true;
            List<NpgsqlDataSource> retired = [];
            foreach (var entry in _dataSources.Values)
            {
                entry.Retired = true;
                if (entry.Acquisitions == 0)
                {
                    retired.Add(entry.DataSource);
                }
            }

            _dataSources.Clear();
            return retired;
        }
    }

    private sealed class CacheEntry(string connectionString, NpgsqlDataSource dataSource, long lastUsedTimestamp)
    {
        public string ConnectionString { get; } = connectionString;
        public NpgsqlDataSource DataSource { get; } = dataSource;
        public int Acquisitions { get; set; }
        public bool Retired { get; set; }
        public long LastUsedTimestamp { get; set; } = lastUsedTimestamp;
    }

    internal sealed class Acquisition(NpgsqlDataSource dataSource, Action release) : IDisposable
    {
        private Action? _release = release;

        public NpgsqlDataSource DataSource { get; } = dataSource;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
