// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.ControlPlane;
using Honua.TestKit;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

internal sealed class PostgresOperationStores(PostgresFixture fixture, string schema) : IAsyncDisposable
{
    public PostgresOperationProposalStore Proposals { get; } = new(fixture.DataSource, schema);
    public PostgresOperationInstanceStore Instances { get; } = new(fixture.DataSource, schema);

    public static async Task<PostgresOperationStores> CreateAsync()
    {
        var fixture = new PostgresFixture();
        try
        {
            await fixture.InitializeAsync();
            return new PostgresOperationStores(fixture, await fixture.CreateIsolatedSchemaAsync(nameof(PostgresOperationStores)));
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    public async Task AssertNoPlaintextAsync(string plaintext)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload::text FROM {schema}.operation_instances UNION ALL SELECT payload::text FROM {schema}.operation_proposals";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Assert.DoesNotContain(plaintext, reader.GetString(0), StringComparison.Ordinal);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await fixture.DropSchemaAsync(schema); }
        finally { await fixture.DisposeAsync(); }
    }
}
