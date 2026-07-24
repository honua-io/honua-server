// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Postgres.Features.Studio;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Studio;

[Collection("Database")]
public sealed class PostgresStudioPackageStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task PackageStore_CreateVersionPublishAndRollback_PersistsImmutableVersionState()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresStudioPackageStoreTests));
        try
        {
            await EnsureStudioTablesAsync(schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresStudioPackageStore(provider, schema);
            var draft = BuildDraft("1=1");

            var created = await store.CreateDraftAsync(draft);
            var loadedDraft = await store.GetDraftAsync(created.DraftId);

            loadedDraft.Should().NotBeNull();
            loadedDraft!.PackageKey.Should().Be("parcels-query");
            loadedDraft.Validation.Status.Should().Be(StudioPackageValidationStatus.Valid);

            var version = await store.CreateVersionAsync(created, "first save", "tester");
            version.VersionNumber.Should().Be(1);
            version.Dependencies.Should().ContainSingle(d => d.Ref == "content.parcels");

            var loadedVersion = await store.GetVersionAsync(version.ItemId, version.VersionId);
            loadedVersion.Should().NotBeNull();
            loadedVersion!.ContentHash.Should().Be(version.ContentHash);
            loadedVersion.Envelope.Body!.Value.GetProperty("where").GetString().Should().Be("1=1");

            var dependencyRows = await CountDependencyRowsAsync(schema, version.VersionId);
            dependencyRows.Should().Be(1);

            var updatedDraft = created with
            {
                Envelope = BuildEnvelope("POPULATION > 1000"),
                Generation = created.Generation,
            };
            var updated = await store.UpdateDraftAsync(updatedDraft);
            updated.Should().NotBeNull();
            var secondVersion = await store.CreateVersionAsync(updated!, "second save", "tester");

            var publication = await store.CreatePublicationRequestAsync(new StudioPublicationRequest
            {
                RequestId = Guid.NewGuid(),
                ItemId = secondVersion.ItemId,
                VersionId = secondVersion.VersionId,
                Intent = secondVersion.Envelope.PublicationIntent,
                Status = StudioPublicationRequestStatus.Accepted,
                Validation = secondVersion.Validation,
                RequestedBy = "tester",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            publication.Status.Should().Be(StudioPublicationRequestStatus.Accepted);

            var rollback = await store.RollbackAsync(
                version.ItemId,
                version.VersionId,
                StudioRollbackPointer.Both,
                "tester",
                "restore first version");

            rollback.Pointers.CurrentVersionId.Should().Be(version.VersionId);
            rollback.Pointers.PublishedVersionId.Should().Be(version.VersionId);

            var originalAfterRollback = await store.GetVersionAsync(version.ItemId, version.VersionId);
            originalAfterRollback.Should().NotBeNull();
            originalAfterRollback!.Envelope.Body!.Value.GetProperty("where").GetString().Should().Be("1=1");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task PackageStore_UpdateDraftWithDuplicatePackageKey_RejectsConflict()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresStudioPackageStoreTests));
        try
        {
            await EnsureStudioTablesAsync(schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresStudioPackageStore(provider, schema);
            var first = await store.CreateDraftAsync(BuildDraft("1=1", "parcels-query"));
            var second = await store.CreateDraftAsync(BuildDraft("1=1", "buildings-query"));

            var conflict = second with
            {
                PackageKey = first.PackageKey,
                Generation = second.Generation,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            var act = async () => await store.UpdateDraftAsync(conflict);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Studio package key conflicts with an existing content item.");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task PackageStore_UpdateDraftWithStaleGeneration_RejectsConflict()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresStudioPackageStoreTests));
        try
        {
            await EnsureStudioTablesAsync(schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresStudioPackageStore(provider, schema);
            var created = await store.CreateDraftAsync(BuildDraft("1=1", "stale-query"));
            var updated = await store.UpdateDraftAsync(created with
            {
                Envelope = BuildEnvelope("POPULATION > 1000"),
                Generation = created.Generation,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            updated.Should().NotBeNull();

            var stale = created with
            {
                Envelope = BuildEnvelope("POPULATION > 5000"),
                Generation = created.Generation,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            var act = async () => await store.UpdateDraftAsync(stale);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Stale draft generation; refresh and retry.");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task PackageStore_CreateVersionWithDifferentDraftItem_RejectsOwnershipMismatch()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresStudioPackageStoreTests));
        try
        {
            await EnsureStudioTablesAsync(schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresStudioPackageStore(provider, schema);
            var created = await store.CreateDraftAsync(BuildDraft("1=1", "owned-draft-query"));
            var tampered = created with { ItemId = Guid.NewGuid() };

            var act = async () => await store.CreateVersionAsync(tampered, "tampered save", "tester");

            await act.Should().ThrowAsync<KeyNotFoundException>()
                .WithMessage("Studio package draft was not found.");
            var wrongItemVersions = await store.ListVersionsAsync(tampered.ItemId);
            wrongItemVersions.Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task PackageStore_PublicationRequestWithForeignVersion_RejectsOwnershipMismatch()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresStudioPackageStoreTests));
        try
        {
            await EnsureStudioTablesAsync(schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresStudioPackageStore(provider, schema);
            var first = await store.CreateDraftAsync(BuildDraft("1=1", "first-publish-query"));
            var second = await store.CreateDraftAsync(BuildDraft("1=1", "second-publish-query"));
            var firstVersion = await store.CreateVersionAsync(first, "first save", "tester");
            var secondVersion = await store.CreateVersionAsync(second, "second save", "tester");

            var act = async () => await store.CreatePublicationRequestAsync(new StudioPublicationRequest
            {
                RequestId = Guid.NewGuid(),
                ItemId = firstVersion.ItemId,
                VersionId = secondVersion.VersionId,
                Intent = secondVersion.Envelope.PublicationIntent,
                Status = StudioPublicationRequestStatus.Accepted,
                Validation = secondVersion.Validation,
                RequestedBy = "tester",
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await act.Should().ThrowAsync<KeyNotFoundException>()
                .WithMessage("Studio content version was not found.");
            var firstPointers = await store.GetPointersAsync(firstVersion.ItemId);
            firstPointers.Should().NotBeNull();
            firstPointers!.PublishedVersionId.Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task PackageStore_PendingPublicationRequest_DoesNotMovePublishedPointer()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresStudioPackageStoreTests));
        try
        {
            await EnsureStudioTablesAsync(schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresStudioPackageStore(provider, schema);
            var draft = await store.CreateDraftAsync(BuildDraft("1=1", "pending-publish-query"));
            var version = await store.CreateVersionAsync(draft, "first save", "tester");

            var request = await store.CreatePublicationRequestAsync(new StudioPublicationRequest
            {
                RequestId = Guid.NewGuid(),
                ItemId = version.ItemId,
                VersionId = version.VersionId,
                Intent = version.Envelope.PublicationIntent,
                Status = StudioPublicationRequestStatus.Pending,
                Validation = version.Validation,
                RequestedBy = "tester",
                CreatedAt = DateTimeOffset.UtcNow,
            });

            request.Status.Should().Be(StudioPublicationRequestStatus.Pending);
            var pointers = await store.GetPointersAsync(version.ItemId);
            pointers.Should().NotBeNull();
            pointers!.CurrentVersionId.Should().Be(version.VersionId);
            pointers.PublishedVersionId.Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task PackageStore_ConcurrentVersionCreates_SerializesVersionNumbers()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresStudioPackageStoreTests));
        try
        {
            await EnsureStudioTablesAsync(schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresStudioPackageStore(provider, schema);
            var draft = await store.CreateDraftAsync(BuildDraft("1=1", "concurrent-query"));
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var saves = Enumerable.Range(0, 4)
                .Select(async index =>
                {
                    await start.Task.ConfigureAwait(false);
                    return await store.CreateVersionAsync(draft, $"save {index}", "tester").ConfigureAwait(false);
                })
                .ToArray();

            start.SetResult();
            var versions = await Task.WhenAll(saves);

            versions.Select(static version => version.VersionNumber)
                .Order()
                .Should()
                .Equal(1, 2, 3, 4);
            var storedVersions = await store.ListVersionsAsync(draft.ItemId);
            storedVersions.Should().HaveCount(4);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ListContentItems_FiltersByFamilyOwnerStateAndPagesViaCursor()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresStudioPackageStoreTests));
        try
        {
            await EnsureStudioTablesAsync(schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresStudioPackageStore(provider, schema);

            var draftOnlyId = Guid.NewGuid();
            var draftOnly = await store.CreateDraftAsync(BuildDraft("1=1", "list-draft-only", draftOnlyId, owner: "bob"));

            var publishedId = Guid.NewGuid();
            var publishedDraft = await store.CreateDraftAsync(BuildDraft("1=1", "list-published", publishedId, owner: "alice"));
            var version = await store.CreateVersionAsync(publishedDraft, "first save", "alice");
            await store.CreatePublicationRequestAsync(new StudioPublicationRequest
            {
                RequestId = Guid.NewGuid(),
                ItemId = version.ItemId,
                VersionId = version.VersionId,
                Intent = version.Envelope.PublicationIntent,
                Status = StudioPublicationRequestStatus.Accepted,
                Validation = version.Validation,
                RequestedBy = "alice",
                CreatedAt = DateTimeOffset.UtcNow,
            });

            var byOwner = await store.ListContentItemsAsync(new StudioContentItemQuery { OwnerId = "bob" });
            byOwner.Items.Should().ContainSingle(i => i.ItemId == draftOnly.ItemId);
            byOwner.Items[0].State.Should().Be(StudioContentItemState.Draft);

            var byState = await store.ListContentItemsAsync(new StudioContentItemQuery { States = [StudioContentItemState.Published] });
            byState.Items.Should().ContainSingle(i => i.ItemId == publishedId);

            var byFamily = await store.ListContentItemsAsync(new StudioContentItemQuery { Families = [StudioPackageFamily.Query] });
            byFamily.Total.Should().Be(2);

            var page1 = await store.ListContentItemsAsync(new StudioContentItemQuery { Limit = 1 });
            page1.Items.Should().HaveCount(1);
            page1.Total.Should().Be(2);
            page1.NextCursor.Should().NotBeNull();

            var page2 = await store.ListContentItemsAsync(new StudioContentItemQuery { Limit = 1, Cursor = page1.NextCursor });
            page2.Items.Should().HaveCount(1);
            page2.NextCursor.Should().BeNull();
            page1.Items[0].ItemId.Should().NotBe(page2.Items[0].ItemId);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ContentItemOwner_SetOnceAtCreate_ImmutableAcrossUpdatesAndVersions()
    {
        // honua-server#3001: studio_content_items.owner_id is populated once, from the owning
        // draft, and never overwritten by a later draft update or version create for the same
        // item -- even when a later UpdateDraftAsync call carries a different OwnerId.
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresStudioPackageStoreTests));
        try
        {
            await EnsureStudioTablesAsync(schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresStudioPackageStore(provider, schema);

            var draft = await store.CreateDraftAsync(BuildDraft("1=1", "owner-immutable-query", owner: "alice"));

            var itemsAfterCreate = await store.ListContentItemsAsync(new StudioContentItemQuery());
            itemsAfterCreate.Items.Should().ContainSingle(i => i.ItemId == draft.ItemId && i.OwnerId == "alice");

            // A later update carrying a different OwnerId must not transfer ownership of the
            // content item (the endpoint layer prevents this for non-admin callers; the store
            // itself never overwrites owner_id on conflict regardless of caller).
            var reassigned = draft with { OwnerId = "mallory", Generation = draft.Generation };
            var updated = await store.UpdateDraftAsync(reassigned);
            updated.Should().NotBeNull();

            var itemsAfterUpdate = await store.ListContentItemsAsync(new StudioContentItemQuery());
            itemsAfterUpdate.Items.Should().ContainSingle(i => i.ItemId == draft.ItemId && i.OwnerId == "alice");

            var version = await store.CreateVersionAsync(updated!, "first save", "mallory");
            version.OwnerId.Should().Be("mallory", "the immutable version snapshots whatever OwnerId the draft carried at save time");

            var itemsAfterVersion = await store.ListContentItemsAsync(new StudioContentItemQuery());
            itemsAfterVersion.Items.Should().ContainSingle(i => i.ItemId == draft.ItemId && i.OwnerId == "alice");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ListDrafts_FiltersByOwnerAndSearchTerm()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresStudioPackageStoreTests));
        try
        {
            await EnsureStudioTablesAsync(schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresStudioPackageStore(provider, schema);
            var alice = await store.CreateDraftAsync(BuildDraft("1=1", "list-alice-query", owner: "alice"));
            await store.CreateDraftAsync(BuildDraft("1=1", "list-bob-query", owner: "bob"));

            var byOwner = await store.ListDraftsAsync(new StudioPackageDraftQuery { OwnerId = "alice" });
            byOwner.Items.Should().ContainSingle(d => d.DraftId == alice.DraftId);

            var bySearch = await store.ListDraftsAsync(new StudioPackageDraftQuery { SearchTerm = "bob" });
            bySearch.Items.Should().ContainSingle(d => d.PackageKey == "list-bob-query");

            var all = await store.ListDraftsAsync(new StudioPackageDraftQuery());
            all.Total.Should().Be(2);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task DeleteDraft_WithNoSavedVersion_RemovesOrphanFromContentItemList()
    {
        // Regression for honua-server#3003 PR review: a draft-only item, deleted before any
        // version is ever saved, must not linger as an unopenable orphan in
        // GET /content-items (no draft, no version, nothing to show or open).
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresStudioPackageStoreTests));
        try
        {
            await EnsureStudioTablesAsync(schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresStudioPackageStore(provider, schema);
            var draft = await store.CreateDraftAsync(BuildDraft("1=1", "orphan-query"));

            var deleted = await store.DeleteDraftAsync(draft.DraftId);
            deleted.Should().BeTrue();

            var items = await store.ListContentItemsAsync(new StudioContentItemQuery());
            items.Items.Should().NotContain(i => i.ItemId == draft.ItemId);
            items.Total.Should().Be(0);

            // The package key must be reusable now that the orphan item is gone.
            var recreated = await store.CreateDraftAsync(BuildDraft("1=1", "orphan-query"));
            recreated.ItemId.Should().NotBe(draft.ItemId);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task DeleteDraft_AfterVersionSaved_KeepsContentItemListed()
    {
        // A draft deleted after its content was saved as a version must not remove the item:
        // the item has openable history (the saved version) even with no remaining draft.
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresStudioPackageStoreTests));
        try
        {
            await EnsureStudioTablesAsync(schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresStudioPackageStore(provider, schema);
            var draft = await store.CreateDraftAsync(BuildDraft("1=1", "kept-query"));
            var version = await store.CreateVersionAsync(draft, "first save", "tester");

            var deleted = await store.DeleteDraftAsync(draft.DraftId);
            deleted.Should().BeTrue();

            var items = await store.ListContentItemsAsync(new StudioContentItemQuery());
            var row = items.Items.Should().ContainSingle(i => i.ItemId == draft.ItemId).Subject;
            row.State.Should().Be(StudioContentItemState.Current);

            // Reopen semantics: a new draft on the existing item must still be possible after
            // the original draft was deleted, and must not be treated as a fresh item.
            var reopened = await store.CreateDraftAsync(draft with
            {
                DraftId = Guid.NewGuid(),
                BaseVersionId = version.VersionId,
                Generation = 1,
            });
            reopened.ItemId.Should().Be(draft.ItemId);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task EnsureStudioTablesAsync(string schema)
    {
        var root = FindRepoRoot();
        // All segments after `root` are fixed literals and can never be rooted, so Path.Combine
        // cannot drop earlier segments here (cs/path-combine false positive). Migration 090 adds
        // studio_content_items.owner_id (honua-server#3001); migration 089 adds the enumeration
        // indexes it's applied alongside for parity with the real deployment sequence.
        foreach (var migrationFile in new[]
                 {
                     "035_CreateStudioPackageLifecycle.sql",
                     "089_AddStudioContentEnumerationIndexes.sql",
                     "090_AddStudioContentItemOwner.sql",
                 })
        {
            var migrationPath = Path.Join(root, "src", "Honua.Server", "Migrations", migrationFile);
            var sql = await File.ReadAllTextAsync(migrationPath);
            sql = sql.Replace("honua.", $"\"{schema}\".", StringComparison.Ordinal);
            await fixture.ExecuteAsync(sql, schema);
        }
    }

    private async Task<int> CountDependencyRowsAsync(string schema, Guid versionId)
    {
        await using var connection = await fixture.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*)
            FROM "{schema}".studio_content_version_dependencies
            WHERE version_id = @version_id
            """;
        command.Parameters.AddWithValue("@version_id", versionId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static StudioPackageDraft BuildDraft(string where, string packageKey = "parcels-query", Guid? itemId = null, string owner = "tester")
    {
        var now = DateTimeOffset.UtcNow;
        return new StudioPackageDraft
        {
            DraftId = Guid.NewGuid(),
            ItemId = itemId ?? Guid.NewGuid(),
            PackageKey = packageKey,
            WorkspaceId = "studio",
            OwnerId = owner,
            Family = StudioPackageFamily.Query,
            Envelope = BuildEnvelope(where),
            Validation = new StudioValidationSummary
            {
                Status = StudioPackageValidationStatus.Valid,
                GeneratedAt = now,
            },
            Generation = 1,
            CreatedBy = owner,
            UpdatedBy = owner,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static StudioPackageEnvelope BuildEnvelope(string where)
    {
        using var body = JsonDocument.Parse($$"""{"where":"{{where}}"}""");
        return new StudioPackageEnvelope
        {
            Family = StudioPackageFamily.Query,
            SchemaVersion = "1.0",
            Format = "studio_query_package.v1",
            Bindings =
            [
                new StudioPackageBinding
                {
                    Key = "source",
                    Kind = "content",
                    Ref = "content.parcels",
                    Crs = "EPSG:4326",
                    Srid = 4326,
                },
            ],
            Dependencies =
            [
                new StudioPackageDependency
                {
                    Kind = "content-item",
                    Ref = "content.parcels",
                    VersionId = "v1",
                },
            ],
            Provenance =
            [
                new StudioProvenanceRef
                {
                    Kind = "prompt",
                    Ref = "prompt-1",
                    Rel = "generated-by",
                },
            ],
            PublicationIntent = new StudioPublicationIntent { Route = "/studio/parcels", Visibility = "organization" },
            Body = body.RootElement.Clone(),
        };
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            // All segments after `current.FullName` are fixed literals and can never be rooted, so
            // Path.Combine cannot drop earlier segments here (cs/path-combine false positive).
            if (File.Exists(Path.Join(current.FullName, "src", "Honua.Server", "Migrations", "035_CreateStudioPackageLifecycle.sql")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root could not be located.");
    }

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource, string schemaName) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SET search_path TO \"{schemaName}\", public;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            try
            {
                var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (connection, transaction);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => operation();
    }
}
