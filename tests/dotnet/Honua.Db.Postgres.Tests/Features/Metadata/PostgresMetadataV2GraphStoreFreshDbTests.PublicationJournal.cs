// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Metadata;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Metadata;

public sealed partial class PostgresMetadataV2GraphStoreFreshDbTests
{
    private static readonly PublicPublicationKey JournalKey = new("publication-a", "version-a");

    [IntegrationTest]
    public Task Reserve_StaleMetadataRevision_PreservesEmptyJournal() => WithJournalAsync(async context =>
    {
        await using var session = await context.Store.OpenAsync(JournalKey);
        var reserve = () => session.ReserveAsync(context.Reservation with { MetadataRevision = context.Reservation.MetadataRevision + 1 });
        await reserve.Should().ThrowAsync<InvalidOperationException>();
        (await session.ReadAsync()).Should().BeNull();
    });

    [IntegrationTest]
    public Task Reserve_CommittedBeforeCrash_RetainsOriginalCleanupIdentity() => WithJournalAsync(async context =>
    {
        await ReserveJournalAsync(context);
        var outstanding = await context.Store.ReadOutstandingAsync();
        outstanding.Should().ContainSingle();
        outstanding[0].Reservation.Should().Be(context.Reservation);
        outstanding[0].State.Should().Be(PublicPublicationState.Pending);
        outstanding[0].PurgeIds.Should().BeEmpty();
    });

    [IntegrationTest]
    public Task Copy_RolledBackPromotion_RetainsPendingCleanup() => WithJournalAsync(async context =>
    {
        await ReserveJournalAsync(context);
        await using (var interrupted = await context.Store.OpenAsync(JournalKey))
        {
            await interrupted.MarkCopyCompleteAsync();
        }
        await using var resumed = await context.Store.OpenAsync(JournalKey);
        (await resumed.ReadAsync())!.State.Should().Be(PublicPublicationState.Pending);
    });

    [IntegrationTest]
    public Task Copy_RevocationAlreadyStarted_RejectsLatePromotion() => WithJournalAsync(async context =>
    {
        await ReserveJournalAsync(context);
        await using (var revoke = await context.Store.OpenAsync(JournalKey))
        {
            await revoke.BeginRevocationAsync();
            await revoke.CommitAsync();
        }
        await using var lateCopy = await context.Store.OpenAsync(JournalKey);
        var promote = () => lateCopy.MarkCopyCompleteAsync();
        await promote.Should().ThrowAsync<InvalidOperationException>();
        (await lateCopy.ReadAsync())!.State.Should().Be(PublicPublicationState.Revoking);
    });

    [IntegrationTest]
    public Task Revocation_MissingReceiptOrChangedDistribution_PreservesOutstandingObligation() => WithJournalAsync(async context =>
    {
        await ReserveJournalAsync(context);
        await using (var revoke = await context.Store.OpenAsync(JournalKey))
        {
            await revoke.BeginRevocationAsync();
            await revoke.RecordOriginDeletedAsync();
            await revoke.RecordPurgeSubmittedAsync(new("original-distribution", ["purge-a", "purge-b"]));
            await revoke.CommitAsync();
        }
        await using var complete = await context.Store.OpenAsync(JournalKey);
        var missing = () => complete.RecordRevocationCompletedAsync(new("original-distribution", ["purge-a"]));
        await missing.Should().ThrowAsync<InvalidOperationException>();
        var changed = () => complete.RecordRevocationCompletedAsync(new("different-distribution", ["purge-a", "purge-b"]));
        await changed.Should().ThrowAsync<InvalidOperationException>();
        (await complete.ReadAsync())!.State.Should().Be(PublicPublicationState.Revoking);
        await complete.RecordRevocationCompletedAsync(new("original-distribution", ["purge-b", "purge-a"]));
        await complete.CommitAsync();
        (await context.Store.ReadOutstandingAsync()).Should().BeEmpty();
    });

    [IntegrationTheory]
    [InlineData("save")]
    [InlineData("activate")]
    public Task CatalogChange_CompletedRevocation_AllowsNewRevision(string operation) => WithJournalAsync(async context =>
    {
        var original = await context.Store.GetCurrentAsync();
        var replacement = original.Graph with { GeneratedAt = DateTimeOffset.UtcNow.AddMinutes(1) };
        var staged = await context.Store.StageAsync(replacement);
        await ReserveJournalAsync(context);
        await using (var revoke = await context.Store.OpenAsync(JournalKey))
        {
            await revoke.BeginRevocationAsync();
            await revoke.RecordOriginDeletedAsync();
            var purge = new PublicPublicationPurge("original-distribution", ["purge-a"]);
            await revoke.RecordPurgeSubmittedAsync(purge);
            await revoke.RecordRevocationCompletedAsync(purge);
            await revoke.CommitAsync();
        }

        var updated = operation == "save"
            ? await context.Store.SaveAsync(replacement, original.Etag)
            : await context.Store.ActivateRevisionAsync(staged.Revision, original.Etag);

        updated.Revision.Should().BeGreaterThan(original.Revision);
        (await context.Store.ReadOutstandingAsync()).Should().BeEmpty();
    });

    [IntegrationTheory]
    [InlineData("save")]
    [InlineData("activate")]
    public Task CatalogChange_BlockedWrite_CommitsRevocationRequest(string operation) => WithJournalAsync(async context =>
    {
        var original = await context.Store.GetCurrentAsync();
        var replacement = original.Graph with { GeneratedAt = DateTimeOffset.UtcNow.AddMinutes(1) };
        var staged = await context.Store.StageAsync(replacement);
        await ReserveJournalAsync(context);

        var change = async () => operation == "save"
            ? await context.Store.SaveAsync(replacement, original.Etag)
            : await context.Store.ActivateRevisionAsync(staged.Revision, original.Etag);
        await change.Should().ThrowAsync<InvalidOperationException>();

        (await context.Store.GetCurrentAsync()).Revision.Should().Be(original.Revision);
        var outstanding = await context.Store.ReadOutstandingAsync();
        outstanding.Should().ContainSingle();
        outstanding[0].State.Should().Be(PublicPublicationState.Revoking);
        outstanding[0].Reservation.Should().Be(context.Reservation);
        outstanding[0].PurgeIds.Should().BeEmpty();
    });

    private static async Task ReserveJournalAsync(JournalContext context)
    {
        await using var session = await context.Store.OpenAsync(JournalKey);
        await session.ReserveAsync(context.Reservation);
        await session.CommitAsync();
    }

    private async Task WithJournalAsync(Func<JournalContext, Task> test)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("public_publication_journal");
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            var store = new PostgresMetadataV2GraphStore(new TestConnectionProvider(fixture.DataSource, schema),
                "Test", FixtureBypassDatabaseSchemaGuard.Instance, schema);
            var original = await store.SaveAsync(new MetadataV2Graph
            {
                Environment = "Test", Revision = 1, GeneratedAt = DateTimeOffset.UtcNow,
                Resources = []
            }, expectedEtag: null);
            var sql = await File.ReadAllTextAsync(RepositoryPaths.Resolve("src", "Honua.Server", "Migrations", "213_CreatePublicPublicationGenerations.sql"));
            await using (var connection = await fixture.DataSource.OpenConnectionAsync())
            {
                using var builder = new NpgsqlCommandBuilder();
                await using var command = new NpgsqlCommand(sql.Replace("$HonuaSchema$", builder.QuoteIdentifier(schema), StringComparison.Ordinal), connection);
                await command.ExecuteNonQueryAsync();
            }
            await test(new(store, new PublicPublicationReservation
            {
                Key = JournalKey, ServiceId = "service-a", LayerIndex = 0, MetadataRevision = original.Revision,
                DestinationBucket = "original-public-bucket", DestinationBaseUrl = "https://cdn.example.test",
                CdnDistributionId = "original-distribution"
            }));
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private sealed record JournalContext(PostgresMetadataV2GraphStore Store, PublicPublicationReservation Reservation);
}
