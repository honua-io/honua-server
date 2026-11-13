// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class ConfigurationGenerationMigrationTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task ConfigurationMutation_AcrossConnections_AdvancesSharedGenerationForEveryWrite()
    {
        await using var harness = await CreateHarnessAsync();
        var initial = await GenerationAsync(harness);
        await harness.ExecuteAsync($"INSERT INTO {harness.Table("configuration_documents")} (kind, key, payload) VALUES ('test', 'one', decode('01', 'hex'))");
        (await GenerationAsync(harness)).Should().Be(initial + 1);
        await harness.ExecuteAsync($"UPDATE {harness.Table("configuration_documents")} SET revision = revision + 1 WHERE key = 'one'");
        (await GenerationAsync(harness)).Should().Be(initial + 2);
        await harness.ExecuteAsync($"DELETE FROM {harness.Table("configuration_documents")} WHERE key = 'one'");
        (await GenerationAsync(harness)).Should().Be(initial + 3);
    }

    [IntegrationTest]
    public async Task ConfigurationMutation_BeforeCommit_RemainsInvisibleAndRollsBackWithGeneration()
    {
        await using var harness = await CreateHarnessAsync();
        var initial = await GenerationAsync(harness);
        await using var connection = await harness.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand($"INSERT INTO {harness.Table("configuration_documents")} (kind, key, payload) VALUES ('test', 'rollback', decode('01', 'hex'))", connection, transaction);
        await command.ExecuteNonQueryAsync();
        (await GenerationAsync(harness)).Should().Be(initial);
        await transaction.RollbackAsync();
        (await GenerationAsync(harness)).Should().Be(initial);
        (await harness.ScalarAsync<long>($"SELECT count(*) FROM {harness.Table("configuration_documents")}")).Should().Be(0);
    }

    [IntegrationTest]
    public async Task CatalogActivation_UsesTheSameGenerationAsConfigurationDocuments()
    {
        await using var harness = await CreateHarnessAsync();
        var initial = await GenerationAsync(harness);
        await harness.ExecuteAsync($"INSERT INTO {harness.Table("metadata_v2_snapshots")} (environment, revision, schema_version, api_version, document, etag, generated_at) VALUES ('test', 1, '2', '2', '{{}}', 'first', now()), ('test', 2, '2', '2', '{{}}', 'second', now())");
        (await GenerationAsync(harness)).Should().Be(initial);
        await harness.ExecuteAsync($"INSERT INTO {harness.Table("metadata_v2_current")} (environment, revision, etag) VALUES ('test', 1, 'first')");
        (await GenerationAsync(harness)).Should().Be(initial + 1);
        await harness.ExecuteAsync($"UPDATE {harness.Table("metadata_v2_current")} SET revision = 2, etag = 'second' WHERE environment = 'test'");
        (await GenerationAsync(harness)).Should().Be(initial + 2);
        await harness.ExecuteAsync($"DELETE FROM {harness.Table("metadata_v2_current")} WHERE environment = 'test'");
        (await GenerationAsync(harness)).Should().Be(initial + 3);
    }

    [IntegrationTest]
    public async Task ConfigurationMutation_ConcurrentWriters_AdvanceOnlyTheirSchemaGeneration()
    {
        await using var harness = await CreateHarnessAsync();
        await using var otherSchema = await CreateHarnessAsync();
        var initial = await GenerationAsync(harness);
        var otherInitial = await GenerationAsync(otherSchema);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => harness.ExecuteAsync(
            $"INSERT INTO {harness.Table("configuration_documents")} (kind, key, payload) VALUES ('test', '{index}', decode('01', 'hex'))")));
        (await GenerationAsync(harness)).Should().Be(initial + 8);
        (await GenerationAsync(otherSchema)).Should().Be(otherInitial);
        (await otherSchema.ScalarAsync<long>($"SELECT count(*) FROM {otherSchema.Table("configuration_documents")}")).Should().Be(0);
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(ConfigurationGenerationMigrationTests));

    private static Task<long> GenerationAsync(ControlPlaneStateHarness harness)
        => harness.ScalarAsync<long>($"SELECT generation FROM {harness.Table("configuration_generation")} WHERE singleton");
}
