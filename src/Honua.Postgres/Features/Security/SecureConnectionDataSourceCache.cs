// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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
    private readonly string? _defaultSchema;

    [RequiresDynamicCode("Calls PostgresDataSourceFactory.ResolveConnectionLimits which binds configuration via ConfigurationBinder.Bind(Object).")]
    [RequiresUnreferencedCode("Calls Microsoft.Extensions.Configuration.ConfigurationBinder.GetValue<T>(String)")]
    public SecureConnectionDataSourceCache(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _schemaHeadersEnabled = configuration.GetValue<bool>("HONUA_TEST_SCHEMA_HEADERS");
        _connectionLimits = PostgresDataSourceFactory.ResolveConnectionLimits(configuration);
        // Preserve the configured default schema so named secure connections
        // get the same search_path embedded in their Options parameter as the
        // default data source. Without this, background/service callers (where
        // ISchemaContext.CurrentSchema is null) fall back to the PostgreSQL
        // default search_path and miss schema-qualified tables.
        _defaultSchema = configuration["Database:Schema"];
    }

    public NpgsqlDataSource GetOrCreate(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));
        }

        var lazy = _dataSources.GetOrAdd(connectionString, key =>
            new Lazy<NpgsqlDataSource>(
                () => PostgresDataSourceFactory.Create(key, _schemaHeadersEnabled, _connectionLimits, _defaultSchema),
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
