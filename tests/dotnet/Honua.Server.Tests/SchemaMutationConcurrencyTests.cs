// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests;

/// <summary>
/// Regression guard for the residual honua-server#1568 <c>40P01: deadlock detected</c> flake.
/// </summary>
/// <remarks>
/// #1568/#2028 serialized schema <em>drop</em>, raw <c>honua.*</c> seed/DDL, and the embedded
/// migration set on the shared schema-mutation advisory lock, but left schema <em>creation</em>
/// a non-participant: the bare <c>CREATE SCHEMA</c> in
/// <see cref="PostgresFixture.CreateIsolatedSchemaInternalAsync"/> and a handful of tests' own
/// <c>ExecuteAsync("CREATE SCHEMA ...")</c>. A per-test <c>CREATE SCHEMA</c> in one collection could
/// then race another collection's locked <c>DROP SCHEMA ... CASCADE</c> — both churn the shared
/// <c>pg_catalog</c> (<c>pg_namespace</c>/<c>pg_class</c>/<c>pg_depend</c>) — and the asymmetric
/// serialization left a residual catalog lock-ordering hazard. This test drives the
/// create + migrate + drop surface from many tasks at once (the parallel-collection pattern) so the
/// schema-mutation surface is exercised concurrently and the serialization is proven by the absence
/// of a deadlock victim escaping the retry budget.
/// </remarks>
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
