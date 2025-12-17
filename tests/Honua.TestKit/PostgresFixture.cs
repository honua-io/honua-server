// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Honua.TestKit;

/// <summary>
/// Shared PostgreSQL + PostGIS fixture for integration tests.
/// Uses Testcontainers to manage container lifecycle.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container;

    public PostgresFixture()
    {
        _container = new PostgreSqlBuilder()
            .WithImage("postgis/postgis:16-3.4")
            .WithDatabase("honua_test")
            .WithUsername("test")
            .WithPassword("test")
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
    /// Get a fresh connection for a test.
    /// </summary>
    public async Task<NpgsqlConnection> GetConnectionAsync()
    {
        return await DataSource.OpenConnectionAsync();
    }

    /// <summary>
    /// Execute raw SQL for test setup.
    /// </summary>
    public async Task ExecuteAsync(string sql)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Clean up test data between tests.
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
}
