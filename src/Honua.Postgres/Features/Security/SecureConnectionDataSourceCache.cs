// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Configuration;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Honua.Postgres.Features.Security;

internal sealed class SecureConnectionDataSourceCache : IDisposable
{
    private readonly ConcurrentDictionary<string, CacheEntry> _dataSources = new(StringComparer.Ordinal);
    private readonly bool _schemaHeadersEnabled;
    private readonly ConnectionLimits _connectionLimits;
    private readonly string? _defaultSchema;

    public SecureConnectionDataSourceCache(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _schemaHeadersEnabled = bool.TryParse(configuration["HONUA_TEST_SCHEMA_HEADERS"], out var schemaHeadersEnabled) && schemaHeadersEnabled;
        _connectionLimits = PostgresDataSourceFactory.ResolveConnectionLimits(configuration);
        // Preserve the configured default schema so named secure connections
        // get the same search_path embedded in their Options parameter as the
        // default data source. Without this, background/service callers (where
        // ISchemaContext.CurrentSchema is null) fall back to the PostgreSQL
        // default search_path and miss schema-qualified tables.
        _defaultSchema = configuration["Database:Schema"];
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

        while (true)
        {
            var entry = _dataSources.GetOrAdd(connectionName, _ => CreateEntry(connectionString));
            if (string.Equals(entry.ConnectionString, connectionString, StringComparison.Ordinal))
            {
                return entry.DataSource.Value;
            }

            // Connection string changed for this logical connection: swap in a fresh
            // data source and dispose the previous one. Npgsql closes its idle pooled
            // connections immediately and rented ones as they are returned, so in-flight
            // commands on the old pool finish normally. Retry on a lost swap race.
            var replacement = CreateEntry(connectionString);
            if (_dataSources.TryUpdate(connectionName, replacement, entry))
            {
                if (entry.DataSource.IsValueCreated)
                {
                    entry.DataSource.Value.Dispose();
                }

                return replacement.DataSource.Value;
            }
        }
    }

    private CacheEntry CreateEntry(string connectionString)
        => new(
            connectionString,
            new Lazy<NpgsqlDataSource>(
                () => PostgresDataSourceFactory.Create(connectionString, _schemaHeadersEnabled, _connectionLimits, _defaultSchema),
                LazyThreadSafetyMode.ExecutionAndPublication));

    public void Dispose()
    {
        foreach (var entry in _dataSources.Values)
        {
            if (entry.DataSource.IsValueCreated)
            {
                entry.DataSource.Value.Dispose();
            }
        }

        _dataSources.Clear();
    }

    private sealed record CacheEntry(string ConnectionString, Lazy<NpgsqlDataSource> DataSource);
}
