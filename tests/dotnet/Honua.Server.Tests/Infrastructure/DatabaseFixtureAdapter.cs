// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit;
using Npgsql;

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// Adapter that wraps PostgresFixture to provide the abstracted IDatabaseFixture interface.
/// This allows Server tests to use database functionality without depending directly on PostgreSQL infrastructure.
/// </summary>
public sealed class DatabaseFixtureAdapter : IDatabaseFixture
{
    private readonly PostgresFixture _postgresFixture;

    public DatabaseFixtureAdapter()
    {
        _postgresFixture = new PostgresFixture();
    }

    public NpgsqlDataSource DataSource => _postgresFixture.DataSource;

    public string ConnectionString => _postgresFixture.ConnectionString;

    public async Task InitializeAsync()
    {
        await _postgresFixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _postgresFixture.DisposeAsync();
    }

    public async Task<string> CreateIsolatedSchemaAsync(string testClassName)
    {
        return await _postgresFixture.CreateIsolatedSchemaAsync(testClassName);
    }

    public async Task DropSchemaAsync(string schemaName)
    {
        await _postgresFixture.DropSchemaAsync(schemaName);
    }

    public async Task ExecuteAsync(string sql, string? schemaName = null)
    {
        await _postgresFixture.ExecuteAsync(sql, schemaName);
    }
}
