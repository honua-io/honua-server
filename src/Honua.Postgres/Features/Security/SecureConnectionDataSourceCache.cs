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
    private readonly ConcurrentDictionary<string, Lazy<NpgsqlDataSource>> _dataSources = new(StringComparer.Ordinal);
    private readonly bool _schemaHeadersEnabled;
    private readonly ConnectionLimits _connectionLimits;

    public SecureConnectionDataSourceCache(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _schemaHeadersEnabled = configuration.GetValue<bool>("HONUA_TEST_SCHEMA_HEADERS");
        _connectionLimits = PostgresDataSourceFactory.ResolveConnectionLimits(configuration);
    }

    public NpgsqlDataSource GetOrCreate(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));
        }

        var lazy = _dataSources.GetOrAdd(connectionString, key =>
            new Lazy<NpgsqlDataSource>(
                () => PostgresDataSourceFactory.Create(key, _schemaHeadersEnabled, _connectionLimits),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
    }

    public void Dispose()
    {
        foreach (var entry in _dataSources.Values)
        {
            if (entry.IsValueCreated)
            {
                entry.Value.Dispose();
            }
        }

        _dataSources.Clear();
    }
}
