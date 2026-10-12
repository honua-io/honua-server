// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Npgsql;

namespace Honua.TestKit.Postgres.Hosting;

/// <summary>Owns a bounded fixture lease and one canonically provisioned database.</summary>
public sealed class PostgresComponentDatabase : IAsyncDisposable
{
    private readonly Lock _disposeGate = new();
    private Task? _disposeTask;

    private readonly PostgresFixture _fixture = new(boundedResources: true);
    private string? _connectionString;

    public string ConnectionString => _connectionString
        ?? throw new InvalidOperationException("Component database has not been provisioned.");

    public async Task InitializeAsync(string seedPath)
    {
        try
        {
            await _fixture.InitializeAsync().ConfigureAwait(false);
            _connectionString = await _fixture.ProvisionDatabaseAsync(seedPath).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        var connection = _connectionString;
        _connectionString = null;
        try
        {
            if (connection is not null)
            {
                await _fixture.DropDatabaseAsync(new NpgsqlConnectionStringBuilder(connection).Database!).ConfigureAwait(false);
            }
        }
        finally
        {
            await _fixture.DisposeAsync().ConfigureAwait(false);
        }
    }
}
