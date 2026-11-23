// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.FeatureStore;

// Reconcile while an unrelated DEFAULT writer holds the committed prefix back (migration 228): the
// analysis reads the state its snapshot sees, and an unchanged live row is not a conflict.
public sealed partial class BranchVersioningIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    public async Task Reconcile_BranchDeleteAfterDefaultRevert_IsCleanAndPostDeletes()
    {
        var store = CreateFeatureStore();
        var feature = await store.CreateAsync(PointsLayerId, BuildFeature("ancestor", 21, 21), CancellationToken.None);
        var version = await CreateVersionManager().CreateAsync(
            new CreateVersionRequest("DeleteAfterRevert", "sde", VersionAccess.Public), CancellationToken.None);
        await store.ApplyEditsAsync(PointsLayerId,
            FeatureEditBatch.Create(deletes: [feature.Id], versionContext: VersionContext.ForVersion(version)),
            CancellationToken.None);
        await SetDefaultNameAsync(feature.Id, "changed");
        await SetDefaultNameAsync(feature.Id, "ancestor");

        var result = await CreateVersionManager().ReconcileAsync(
            version.VersionId, VersionReconcilePolicy.None, VersionConflictDetection.ByObject, CancellationToken.None);

        result.Conflicts.Should().BeEmpty("DEFAULT ends equal to the row the branch deleted");
        (await CreateVersionManager().PostAsync(version.VersionId, CancellationToken.None)).Posted.Should().BeTrue();
        var stored = await store.QueryAsync(PointsLayerId, new FeatureQuery(), CancellationToken.None);
        stored.Items.Should().NotContain(item => item.Id == feature.Id);
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    public async Task Reconcile_BothDeletedWithDefaultDeleteAbovePrefix_IsClean()
    {
        var store = CreateFeatureStore();
        var feature = await store.CreateAsync(PointsLayerId, BuildFeature("ancestor", 22, 22), CancellationToken.None);
        var version = await CreateVersionManager().CreateAsync(
            new CreateVersionRequest("BothDeleted", "sde", VersionAccess.Public), CancellationToken.None);
        await store.ApplyEditsAsync(PointsLayerId,
            FeatureEditBatch.Create(deletes: [feature.Id], versionContext: VersionContext.ForVersion(version)),
            CancellationToken.None);
        await SetDefaultNameAsync(feature.Id, "updated");
        _ = await CreateTracker().GetCurrentGenerationAsync();

        await using var held = await HoldUnrelatedDefaultWriterAsync(store);
        await store.ApplyEditsAsync(PointsLayerId, FeatureEditBatch.Create(deletes: [feature.Id]), CancellationToken.None);
        var result = await CreateVersionManager().ReconcileAsync(
            version.VersionId, VersionReconcilePolicy.None, VersionConflictDetection.ByObject, CancellationToken.None);
        await held.Transaction.CommitAsync();

        result.Conflicts.Should().BeEmpty("both sides deleted the row");
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    public async Task Reconcile_ByObjectAfterVersionWinsAbovePrefix_StaysClean()
    {
        var store = CreateFeatureStore();
        var feature = await store.CreateAsync(PointsLayerId, BuildFeature("ancestor", 23, 23), CancellationToken.None);
        var version = await CreateVersionManager().CreateAsync(
            new CreateVersionRequest("VersionWinsAbovePrefix", "sde", VersionAccess.Public), CancellationToken.None);
        await store.ApplyEditsAsync(PointsLayerId,
            FeatureEditBatch.Create(
                updates: ImmutableArray.Create(BuildFeature("branch", 23, 23, feature.Id)),
                versionContext: VersionContext.ForVersion(version)),
            CancellationToken.None);

        await using (var held = await HoldUnrelatedDefaultWriterAsync(store))
        {
            await SetDefaultNameAsync(feature.Id, "default");
            var resolved = await CreateVersionManager().ReconcileAsync(
                version.VersionId, VersionReconcilePolicy.VersionWins, VersionConflictDetection.ByObject, CancellationToken.None);
            resolved.CanPost.Should().BeTrue();
            await held.Transaction.CommitAsync();
        }

        var again = await CreateVersionManager().ReconcileAsync(
            version.VersionId, VersionReconcilePolicy.None, VersionConflictDetection.ByObject, CancellationToken.None);
        again.Conflicts.Should().BeEmpty("the resolved row already carries the live DEFAULT image as its base");
    }

    private PostgresChangeTracker CreateTracker()
        => new(new TestDatabaseConnectionProvider(_fixture.DataSource, () => _schema));

    private async Task SetDefaultNameAsync(long featureId, string name)
    {
        await using var connection = await OpenSchemaConnectionAsync();
        await using var update = new NpgsqlCommand("""
            UPDATE features SET attributes = jsonb_set(attributes, '{name}', to_jsonb(@name::text))
            WHERE layer_id = @layer AND objectid = @id
            """, connection);
        update.Parameters.AddWithValue("name", name);
        update.Parameters.AddWithValue("layer", PointsLayerId);
        update.Parameters.AddWithValue("id", featureId);
        (await update.ExecuteNonQueryAsync()).Should().Be(1);
    }

    // An open DEFAULT edit to another feature, allocated before the edits under test, holds the prefix.
    private async Task<HeldWriter> HoldUnrelatedDefaultWriterAsync(PostgresFeatureStoreRefactored store)
    {
        var other = await store.CreateAsync(PointsLayerId, BuildFeature("unrelated", 29, 29), CancellationToken.None);
        var connection = await OpenSchemaConnectionAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using var update = new NpgsqlCommand(
            "UPDATE features SET attributes = attributes WHERE layer_id = @layer AND objectid = @id",
            connection,
            transaction);
        update.Parameters.AddWithValue("layer", PointsLayerId);
        update.Parameters.AddWithValue("id", other.Id);
        (await update.ExecuteNonQueryAsync()).Should().Be(1);
        return new HeldWriter(connection, transaction);
    }

    private sealed class HeldWriter(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public NpgsqlTransaction Transaction => transaction;

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
