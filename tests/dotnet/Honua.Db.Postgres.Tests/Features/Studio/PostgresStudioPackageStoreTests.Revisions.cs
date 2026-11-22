// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Db.Postgres.Features.Studio;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Studio;

public sealed partial class PostgresStudioPackageStoreTests
{
    [IntegrationTest]
    public async Task UpdateDraftAsync_ArchivesPriorEnvelopeInTheSameTransaction()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("StudioDraftRevisions");
        try
        {
            await EnsureStudioTablesAsync(schema);
            var store = new PostgresStudioPackageStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var draft = await store.CreateDraftAsync(BuildDraft("original", "revision-map"));
            await store.UpdateDraftAsync(draft with { Envelope = BuildEnvelope("changed") });

            await using var connection = await fixture.GetConnectionAsync(schema);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT envelope->'body'->>'where'
                FROM "{schema}".studio_draft_revisions
                WHERE draft_id = @draft AND generation = 1
                """;
            command.Parameters.AddWithValue("@draft", draft.DraftId);
            (await command.ExecuteScalarAsync()).Should().Be("original");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task UpdateDraftAsync_RevisionsRoundTripAcrossStoresAndCascadeOnDelete()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("StudioRevisionRoundTrip");
        try
        {
            await EnsureStudioTablesAsync(schema);
            var store = new PostgresStudioPackageStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var original = BuildCompositionRevisionEnvelope();
            var draft = await store.CreateDraftAsync(BuildDraft("original", "revision-round-trip") with { Envelope = original });
            var changed = (await store.UpdateDraftAsync(draft with { Envelope = BuildEnvelope("removed") }))!;
            var freshStore = new PostgresStudioPackageStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var revision = (await freshStore.GetDraftRevisionAsync(draft.DraftId, draft.Generation))!;
            JsonElement.DeepEquals(JsonSerializer.SerializeToElement(original), JsonSerializer.SerializeToElement(revision.Envelope))
                .Should().BeTrue();
            revision.UpdatedBy.Should().Be(draft.UpdatedBy);
            revision.UpdatedAt.Should().BeCloseTo(draft.UpdatedAt, TimeSpan.FromMicroseconds(1));
            var restored = (await freshStore.UpdateDraftAsync(changed with { Envelope = revision.Envelope }))!;
            restored.Generation.Should().Be(3);
            JsonElement.DeepEquals(restored.Envelope.Body!.Value, original.Body!.Value).Should().BeTrue();
            await AssertRevisionPagesAsync(freshStore, draft.DraftId);
            (await freshStore.GetDraftRevisionAsync(Guid.NewGuid(), draft.Generation)).Should().BeNull();
            await freshStore.Invoking(s => s.UpdateDraftAsync(draft)).Should().ThrowAsync<InvalidOperationException>();
            (await freshStore.ListDraftRevisionsAsync(new(draft.DraftId))).Should().HaveCount(2);
            await freshStore.DeleteDraftAsync(draft.DraftId, restored.Generation);
            (await freshStore.ListDraftRevisionsAsync(new(draft.DraftId))).Should().BeEmpty();
            (await freshStore.GetDraftRevisionAsync(draft.DraftId, 1)).Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static StudioPackageEnvelope BuildCompositionRevisionEnvelope() => BuildEnvelope("original") with
    {
        Body = JsonSerializer.SerializeToElement(new
        {
            maps = new[] { new { id = "map", layerIds = new[] { "roads", "parcels" } } },
            layers = new[] { new { id = "roads", sourceId = "source", styleId = "styled" } },
            sources = new[] { new { id = "source", bindingKey = "roads" } },
            styles = new[] { new { id = "styled", color = "#001122" } },
        }),
    };

    private static async Task AssertRevisionPagesAsync(PostgresStudioPackageStore store, Guid draftId)
    {
        var page = await store.ListDraftRevisionsAsync(new(draftId, Limit: 1));
        page.Should().ContainSingle().Which.Generation.Should().Be(2);
        (await store.ListDraftRevisionsAsync(new(draftId, BeforeGeneration: 2)))
            .Should().ContainSingle().Which.Generation.Should().Be(1);
    }

    [IntegrationTest]
    public async Task UpdateDraftAsync_FailedItemUpsertRollsBackRevisionAndDraft()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("StudioRevisionRollback");
        try
        {
            await EnsureStudioTablesAsync(schema);
            var store = new PostgresStudioPackageStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var draft = await store.CreateDraftAsync(BuildDraft("original", "revision-rollback"));
            await fixture.ExecuteAsync($"""
                ALTER TABLE "{schema}".studio_content_items
                ADD CONSTRAINT reject_revision_key CHECK (package_key <> 'reject-revision');
                """);
            await store.Invoking(s => s.UpdateDraftAsync(draft with { PackageKey = "reject-revision" }))
                .Should().ThrowAsync<Npgsql.PostgresException>();
            (await store.GetDraftAsync(draft.DraftId))!.Generation.Should().Be(draft.Generation);
            (await store.ListDraftRevisionsAsync(new(draft.DraftId))).Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task RestoreDraftRevisionAsync_RestoresStyledRemovedLayerAndRejectsConcurrentStaleWrite()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("StudioRevisionRestore");
        try
        {
            await EnsureStudioTablesAsync(schema);
            var store = new PostgresStudioPackageStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var registry = new StudioPackageFamilyRegistry(store);
            var lifecycle = new StudioPackageLifecycleService(
                store, registry, new StudioPackageValidator(registry, TimeProvider.System), TimeProvider.System);
            var styledEnvelope = BuildCompositionRevisionEnvelope() with
            {
                Bindings = [new StudioPackageBinding { Key = "roads", Kind = "layer", Ref = "services/city/layers/roads" }],
                Dependencies = [new StudioPackageDependency { Kind = "style", Ref = "styles/roads-styled" }],
            };
            var (created, removed) = await CreateStyledThenRemovedDraftAsync(lifecycle, styledEnvelope);
            var restoreCommand = new RestoreStudioDraftRevisionCommand
            {
                TargetGeneration = created.Generation,
                ExpectedGeneration = removed.Generation,
                ActorId = "owner-a",
            };

            var restored = (await lifecycle.RestoreDraftRevisionAsync(created.DraftId, restoreCommand))!;

            restored.Should().BeEquivalentTo(new { Generation = 3L, created.ItemId, created.OwnerId, created.WorkspaceId });
            var freshStore = new PostgresStudioPackageStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            AssertStyledLayerRestored((await freshStore.GetDraftAsync(created.DraftId))!, styledEnvelope);
            await lifecycle.Invoking(s => s.RestoreDraftRevisionAsync(created.DraftId, restoreCommand))
                .Should().ThrowAsync<InvalidOperationException>();
            await store.Invoking(s => s.UpdateDraftAsync(restored with { Generation = removed.Generation }))
                .Should().ThrowAsync<InvalidOperationException>();
            (await freshStore.GetDraftAsync(created.DraftId))!.Generation.Should().Be(3);
            (await store.ListDraftRevisionsAsync(new(created.DraftId))).Should().HaveCount(2);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task PreviewPlanAsync_LeavesStoredDraftGenerationAuditAndRevisionsUnchanged()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("StudioPreviewReadOnly");
        try
        {
            await EnsureStudioTablesAsync(schema);
            var store = new PostgresStudioPackageStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var registry = new StudioPackageFamilyRegistry(store);
            var lifecycle = new StudioPackageLifecycleService(
                store, registry, new StudioPackageValidator(registry, TimeProvider.System), TimeProvider.System);
            var created = await lifecycle.CreateDraftAsync(new CreateStudioPackageDraftCommand
            {
                PackageKey = "readonly-preview",
                Envelope = BuildEnvelope("1=1"),
                ActorId = "author-a",
            });
            var before = (await store.GetDraftAsync(created.DraftId))!;

            var plan = await lifecycle.PreviewPlanAsync(created.DraftId, "reviewer-b");

            plan.Should().NotBeNull();
            var reloaded = (await store.GetDraftAsync(created.DraftId))!;
            reloaded.Generation.Should().Be(before.Generation);
            reloaded.UpdatedBy.Should().Be(before.UpdatedBy);
            reloaded.UpdatedAt.Should().Be(before.UpdatedAt);
            reloaded.Validation.GeneratedAt.Should().Be(before.Validation.GeneratedAt);
            (await store.ListDraftRevisionsAsync(new(created.DraftId))).Should().BeEmpty();
            (await lifecycle.PreviewPlanAsync(Guid.NewGuid(), "reviewer-b")).Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static async Task<(StudioPackageDraft Created, StudioPackageDraft Removed)> CreateStyledThenRemovedDraftAsync(
        StudioPackageLifecycleService lifecycle,
        StudioPackageEnvelope styledEnvelope)
    {
        var created = await lifecycle.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "styled-restore",
            WorkspaceId = "ws-city",
            OwnerId = "owner-a",
            Envelope = styledEnvelope,
            ActorId = "owner-a",
        });
        var removed = (await lifecycle.UpdateDraftAsync(created.DraftId, new UpdateStudioPackageDraftCommand
        {
            PackageKey = created.PackageKey,
            WorkspaceId = created.WorkspaceId,
            OwnerId = created.OwnerId,
            Envelope = BuildEnvelope("removed") with { Bindings = [], Dependencies = [] },
            Generation = created.Generation,
            ActorId = "owner-a",
        }))!;
        return (created, removed);
    }

    private static void AssertStyledLayerRestored(StudioPackageDraft reloaded, StudioPackageEnvelope styledEnvelope)
    {
        reloaded.Generation.Should().Be(3);
        JsonElement.DeepEquals(reloaded.Envelope.Body!.Value, styledEnvelope.Body!.Value).Should().BeTrue();
        reloaded.Envelope.Bindings.Should().ContainSingle(b => b.Key == "roads" && b.Ref == "services/city/layers/roads");
        reloaded.Envelope.Dependencies.Should().ContainSingle(d => d.Kind == "style" && d.Ref == "styles/roads-styled");
    }
}
