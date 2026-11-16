// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;
using Npgsql;

namespace Honua.Server.Tests.Realtime;

// Feature streaming depends on the production change-tracking and notification migrations,
// which the shared baseline fixture intentionally does not apply.
public sealed class FeatureChangeDatabaseFixture : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();
    private string? _connectionString;

    public string ConnectionString => _connectionString
        ?? throw new InvalidOperationException("Feature-change database is not initialized.");

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();
        try
        {
            _connectionString = await _postgres.ProvisionDatabaseAsync(
                RepositoryPaths.Resolve("tests", "seed", "odata.yaml"));
            DataSource = NpgsqlDataSource.Create(ConnectionString);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (DataSource is not null)
            {
                await DataSource.DisposeAsync();
            }

            if (_connectionString is not null)
            {
                var databaseName = new NpgsqlConnectionStringBuilder(_connectionString).Database!;
                _connectionString = null;
                await _postgres.DropDatabaseAsync(databaseName);
            }
        }
        finally
        {
            await _postgres.DisposeAsync();
        }
    }
}
