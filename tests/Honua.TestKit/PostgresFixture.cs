// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Honua.TestKit;

/// <summary>
/// Shared PostgreSQL + PostGIS fixture for integration tests.
/// Uses Testcontainers to manage container lifecycle.
/// Supports schema-based test isolation for parallel execution.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private static readonly ConcurrentDictionary<string, int> _schemaCounters = new();
    private readonly PostgreSqlContainer _container;

    public PostgresFixture()
    {
        _container = new PostgreSqlBuilder()
            .WithImage("postgis/postgis:18-3.6")
            .WithDatabase("honua_test")
            .WithUsername("test")
            .WithPassword("test")
            .WithCommand("-c", "max_connections=200")
            .Build();
    }

    public string ConnectionString => _container.GetConnectionString();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        DataSource = NpgsqlDataSource.Create(ConnectionString);

        // Enable PostGIS extension
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS postgis;";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>
    /// Creates an isolated schema for a test.
    /// Schema names are unique per test class to support parallel execution.
    /// </summary>
    /// <param name="testClassName">Name of the test class (for schema naming)</param>
    /// <returns>Schema name to use for the test</returns>
    public async Task<string> CreateIsolatedSchemaAsync(string testClassName)
    {
        var counter = _schemaCounters.AddOrUpdate(testClassName, 1, (_, c) => c + 1);
        var schemaName = $"test_{SanitizeSchemaName(testClassName)}_{counter}_{Guid.NewGuid():N}".ToLowerInvariant();

        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE SCHEMA {schemaName};
            SET search_path TO {schemaName}, public;
            """;
        await cmd.ExecuteNonQueryAsync();

        return schemaName;
    }

    /// <summary>
    /// Drops an isolated schema created for a test.
    /// </summary>
    public async Task DropSchemaAsync(string schemaName)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP SCHEMA IF EXISTS {schemaName} CASCADE;";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Get a connection configured for a specific schema.
    /// </summary>
    public async Task<NpgsqlConnection> GetConnectionAsync(string? schemaName = null)
    {
        var conn = await DataSource.OpenConnectionAsync();

        if (schemaName is not null)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SET search_path TO {schemaName}, public;";
            await cmd.ExecuteNonQueryAsync();
        }

        return conn;
    }

    /// <summary>
    /// Execute raw SQL for test setup in a specific schema.
    /// </summary>
    public async Task ExecuteAsync(string sql, string? schemaName = null)
    {
        await using var conn = await GetConnectionAsync(schemaName);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Clean up test data in the public schema (legacy method).
    /// Prefer schema-based isolation for parallel execution.
    /// </summary>
    public async Task ResetAsync()
    {
        await ExecuteAsync("""
            DO $$
            DECLARE
                r RECORD;
            BEGIN
                FOR r IN (SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename != 'spatial_ref_sys') LOOP
                    EXECUTE 'TRUNCATE TABLE ' || quote_ident(r.tablename) || ' CASCADE';
                END LOOP;
            END $$;
            """);
    }

    private static string SanitizeSchemaName(string name)
    {
        return new string(name
            .Where(c => char.IsLetterOrDigit(c) || c == '_')
            .ToArray());
    }
}
