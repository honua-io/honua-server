// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.FeatureStore;

public sealed partial class BranchVersioningIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    public async Task CreateAsync_DefaultWriteInFlight_UsesCommittedMergeBaseAndRetainsLateChange()
    {
        var feature = await CreateFeatureStore().CreateAsync(
            PointsLayerId, BuildFeature("committed", 1, 1), CancellationToken.None);
        var tracker = new PostgresChangeTracker(new TestDatabaseConnectionProvider(_fixture.DataSource, () => _schema));
        var baseline = await tracker.GetCurrentGenerationAsync();
        await using var writer = await OpenSchemaConnectionAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await using (var update = new NpgsqlCommand("""
            UPDATE features SET attributes = '{"name":"late-commit"}'::jsonb WHERE objectid = @id
            """, writer, transaction))
        {
            update.Parameters.AddWithValue("id", feature.Id);
            (await update.ExecuteNonQueryAsync()).Should().Be(1);
        }

        var branch = await CreateVersionManager().CreateAsync(
            new CreateVersionRequest("InFlightMergeBase", "sde", VersionAccess.Public));
        branch.CommonAncestorGeneration.Should().Be(baseline);
        branch.BranchGeneration.Should().Be(baseline);
        await transaction.CommitAsync();

        var changes = await tracker.GetChangesSinceAsync(branch.CommonAncestorGeneration, [PointsLayerId]);
        changes.Should().Contain(change => change.ObjectId == feature.Id && change.Generation > baseline);
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    public async Task CreateAsync_RolledBackAllocation_UsesCommittedMergeBase()
    {
        var feature = await CreateFeatureStore().CreateAsync(
            PointsLayerId, BuildFeature("committed", 1, 1), CancellationToken.None);
        var tracker = new PostgresChangeTracker(new TestDatabaseConnectionProvider(_fixture.DataSource, () => _schema));
        var baseline = await tracker.GetCurrentGenerationAsync();
        await using var writer = await OpenSchemaConnectionAsync();
        await using (var transaction = await writer.BeginTransactionAsync())
        {
            await using var update = new NpgsqlCommand("""
                UPDATE features SET attributes = '{"name":"rolled-back"}'::jsonb WHERE objectid = @id
                """, writer, transaction);
            update.Parameters.AddWithValue("id", feature.Id);
            (await update.ExecuteNonQueryAsync()).Should().Be(1);
            await transaction.RollbackAsync();
        }

        var branch = await CreateVersionManager().CreateAsync(
            new CreateVersionRequest("RolledBackMergeBase", "sde", VersionAccess.Public));
        branch.CommonAncestorGeneration.Should().Be(baseline);
        branch.BranchGeneration.Should().Be(baseline);
    }
}
