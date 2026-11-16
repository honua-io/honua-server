// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests;

[Protocol(TestProtocols.TestQuality)]
[Collection("Database.CoreFeatureStore")]
public sealed class SchemaMutationConcurrencyTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();

    public async Task InitializeAsync() => await _postgres.InitializeAsync();

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ConcurrentSchemaCreateMigrateDrop_DoesNotDeadlock()
    {
        const int workers = 16;
        const int cyclesPerWorker = 4;
        var migrationsAssembly = Assembly.GetAssembly(typeof(Program))!;
        var baseConnectionString = _postgres.ConnectionString;

        var tasks = Enumerable.Range(0, workers).Select(worker => Task.Run(async () =>
        {
            for (var cycle = 0; cycle < cyclesPerWorker; cycle++)
            {
                // CREATE SCHEMA (now serialized), the embedded migration set (creates the literal,
                // global honua schema/tables), and DROP SCHEMA ... CASCADE all run concurrently
                // across workers — the residual deadlock combination.
                var schema = await _postgres.CreateIsolatedSchemaInternalAsync(
                    $"{nameof(SchemaMutationConcurrencyTests)}_{worker}", applySeed: false);

                var result = await _postgres.RunEmbeddedMigrationsUnderLockAsync(
                    schema, baseConnectionString, migrationsAssembly);
                result.Successful.Should().BeTrue(result.Error?.ToString());

                await _postgres.DropSchemaAsync(schema);
            }
        }));

        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync<PostgresException>(
            "all schema create/migrate/drop DDL must serialize on the shared advisory lock rather than deadlocking (40P01)");
    }
}
