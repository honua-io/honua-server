// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Npgsql;
using Xunit;

namespace Honua.Protocols.OData.ComponentTests;

/// <summary>Candidate fixture qualification, separate from the HTTP parity measurement.</summary>
[Trait("Category", "FixtureLifecycle")]
[Trait("Tier", "Integration")]
public sealed class PostgresFixtureLifecycleTests
{
    [Fact]
    public void LegacyXunitFixtureActivation_HasOneParameterlessPublicConstructor()
    {
        // xUnit collection fixtures reject multiple public constructors.
        // The bounded pilot entry point must not change that activation contract.
        var constructor = Assert.Single(typeof(PostgresFixture).GetConstructors());
        Assert.Empty(constructor.GetParameters());
    }

    [Fact]
    public async Task ProvisionedDatabase_PinsCanonicalSeedSchemaForRuntimeConnections()
    {
        var postgres = new PostgresFixture(boundedResources: true);
        string? connectionString = null;
        try
        {
            await postgres.InitializeAsync();
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tests/seed/odata.yaml")))
                directory = directory.Parent;
            var seed = Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Canonical OData seed not found."), "tests/seed/odata.yaml");
            connectionString = await postgres.ProvisionODataDatabaseAsync(seed);
            new NpgsqlConnectionStringBuilder(connectionString).SearchPath.Should().Be("public");
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT current_schema(), to_regclass('features') = to_regclass('public.features'), (SELECT count(*) FROM features WHERE layer_id = 0)";
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("public");
            reader.GetBoolean(1).Should().BeTrue("the host must resolve the same relation as the canonical seed");
            reader.GetInt64(2).Should().Be(15);
        }
        finally
        {
            try
            {
                if (connectionString is not null)
                    await postgres.DropDatabaseAsync(new NpgsqlConnectionStringBuilder(connectionString).Database!);
            }
            finally
            {
                await postgres.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task DisposingUninitializedFixture_DoesNotReleaseActiveBoundedDatabase()
    {
        var active = new PostgresFixture(boundedResources: true);
        try
        {
            await active.InitializeAsync();
            await using (var before = await active.DataSource.OpenConnectionAsync())
            {
                await using var command = before.CreateCommand();
                command.CommandText = "SELECT 1";
                (await command.ExecuteScalarAsync()).Should().Be(1);
            }

            var uninitialized = new PostgresFixture(boundedResources: true);
            await uninitialized.DisposeAsync();
            await uninitialized.DisposeAsync();

            // The shared pool itself must remain usable. A leaked reference decrement
            // disposes this data source even if an external database remains running.
            await using var after = await active.DataSource.OpenConnectionAsync();
            await using var retained = after.CreateCommand();
            retained.CommandText = "SELECT 1";
            (await retained.ExecuteScalarAsync()).Should().Be(1);
        }
        finally
        {
            await active.DisposeAsync();
        }
    }
}
