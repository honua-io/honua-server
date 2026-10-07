// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Db.Postgres.Features.Attachments;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed partial class GeoservicesImportServiceAttachmentImportTests
{
    private const string ReducedInventory = """
        {"attachmentGroups":[{"parentObjectId":2,"attachmentInfos":[
          {"id":1003,"name":"updated.txt","contentType":"text/plain","size":7,"keywords":"updated"}]}]}
        """;

    [Fact]
    public async Task ImportLayerAsync_UnsupportedAttachmentStore_RequiresReviewWithoutUploadingDuplicates()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("AttachmentUnsupportedStore");
        try
        {
            var store = new Mock<Honua.Core.Features.Attachments.Abstractions.IAttachmentStore>(MockBehavior.Strict);
            var service = CreateService(new AttachmentFeatureServerHandler(), store.Object,
                new StubLayerPublishingService(42), schema);
            var result = await service.ImportLayerAsync(ReimportRequest(schema));
            result.Success.Should().BeFalse();
            result.NeedsReview.Should().BeTrue();
            result.FailedAttachments.Should().Be(3);
            result.Warnings.Should().Contain(w => w.Contains("source identity reconciliation", StringComparison.Ordinal));
            store.VerifyNoOtherCalls();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task ImportLayerAsync_IdenticalReimport_PreservesAttachmentIdsWithoutDuplicatingRowsOrFiles()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("AttachmentReimport");
        try
        {
            var storage = new ImportStorage();
            var store = await CreatePersistentStoreAsync(schema, storage);
            var handler = new AttachmentFeatureServerHandler();
            var service = CreateService(handler, store, new StubLayerPublishingService(42), schema);
            (await service.ImportLayerAsync(ReimportRequest(schema))).Success.Should().BeTrue();
            var before = await ReadStoredAsync(schema);

            var repeated = await service.ImportLayerAsync(ReimportRequest(schema));

            repeated.Success.Should().BeTrue();
            repeated.AttachmentCount.Should().Be(3);
            var after = await ReadStoredAsync(schema);
            after.Select(a => a.Id).Should().BeEquivalentTo(before.Select(a => a.Id));
            after.Should().HaveCount(3);
            storage.Files.Keys.Should().BeEquivalentTo(after.Select(a => a.StoragePath));
            storage.Files.Keys.Should().NotIntersectWith(before.Select(a => a.StoragePath));
            (await PendingCleanupAsync(schema)).Should().Be(0);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task ImportLayerAsync_RemovedParentAndChangedAttachment_RebindsAndReconcilesImportedSet()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("AttachmentRemovedParent");
        try
        {
            var storage = new ImportStorage();
            var store = await CreatePersistentStoreAsync(schema, storage);
            var handler = new AttachmentFeatureServerHandler();
            var service = CreateService(handler, store, new StubLayerPublishingService(42), schema);
            (await service.ImportLayerAsync(ReimportRequest(schema))).Success.Should().BeTrue();
            var before = await ReadStoredAsync(schema);
            var photo = before.Single(a => a.Filename == "photo2.jpg");

            handler.FeatureCount = 1;
            handler.FeaturesJson = """
                {"features":[{"attributes":{"OBJECTID":2,"Name":"Beta"},"geometry":{"x":-157.2,"y":21.4}}],
                 "exceededTransferLimit":false,"spatialReference":{"wkid":4326}}
                """;
            handler.InventoryJson = ReducedInventory;
            handler.Payload = "changed";
            var result = await service.ImportLayerAsync(ReimportRequest(schema));

            result.Success.Should().BeTrue();
            result.AttachmentCount.Should().Be(1);
            var current = (await ReadStoredAsync(schema)).Should().ContainSingle().Subject;
            current.Id.Should().Be(photo.Id);
            current.FeatureId.Should().Be(1, "the surviving source parent has a new target object id");
            current.FeatureId.Should().NotBe(photo.FeatureId);
            current.Filename.Should().Be("updated.txt");
            current.ContentType.Should().Be("text/plain");
            current.Keywords.Should().Be("updated");
            Encoding.UTF8.GetString(storage.Files[current.StoragePath]).Should().Be("changed");
            storage.Files.Should().ContainSingle();
            storage.Files.Keys.Should().NotIntersectWith(before.Select(a => a.StoragePath));
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportLayerAsync_EmptyAttachmentInventory_RemovesPriorImportedAttachments(bool hasAttachments)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("AttachmentEmptyInventory");
        try
        {
            var storage = new ImportStorage();
            var store = await CreatePersistentStoreAsync(schema, storage);
            var handler = new AttachmentFeatureServerHandler();
            var service = CreateService(handler, store, new StubLayerPublishingService(42), schema);
            (await service.ImportLayerAsync(ReimportRequest(schema))).Success.Should().BeTrue();

            handler.HasAttachments = hasAttachments;
            handler.InventoryJson = """{"attachmentGroups":[]}""";
            var result = await service.ImportLayerAsync(ReimportRequest(schema));

            result.Success.Should().BeTrue();
            result.AttachmentCount.Should().Be(0);
            (await ReadStoredAsync(schema)).Should().BeEmpty();
            storage.Files.Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Theory]
    [InlineData("download")]
    [InlineData("partial")]
    [InlineData("inventory")]
    [InlineData("cancel")]
    [InlineData("metadata")]
    public async Task ImportLayerAsync_InterruptedCopy_RetainsPriorSetAndRetryDoesNotDuplicate(string failure)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("AttachmentRetry");
        try
        {
            var storage = new ImportStorage();
            var store = await CreatePersistentStoreAsync(schema, storage);
            var handler = new AttachmentFeatureServerHandler();
            var service = CreateService(handler, store, new StubLayerPublishingService(42), schema);
            (await service.ImportLayerAsync(ReimportRequest(schema))).Success.Should().BeTrue();
            var before = await ReadStoredAsync(schema);

            var partiallyCopied = failure is "partial" or "cancel";
            handler.InventoryJson = partiallyCopied
                ? """{"attachmentGroups":[{"parentObjectId":2,"attachmentInfos":[{"id":1003,"name":"updated.txt","contentType":"text/plain","size":7},{"id":1004,"name":"new.txt","contentType":"text/plain","size":7}]}]}"""
                : ReducedInventory;
            handler.FailedDownload = failure == "download" ? 1003 : failure == "partial" ? 1004 : null;
            handler.FailInventory = failure == "inventory";
            storage.InvalidSize = failure == "metadata";
            using var cancellation = new CancellationTokenSource();
            if (failure == "cancel")
            {
                var downloads = 0;
                handler.BeforeDownload = () =>
                {
                    if (++downloads == 2)
                    {
                        cancellation.Cancel();
                    }
                };
                Func<Task> interrupted = async () => await service.ImportLayerAsync(ReimportRequest(schema), cancellation.Token);
                await interrupted.Should().ThrowAsync<OperationCanceledException>();
            }
            else
            {
                var result = await service.ImportLayerAsync(ReimportRequest(schema));
                result.Success.Should().BeFalse();
                result.NeedsReview.Should().BeTrue();
            }

            var retained = await ReadStoredAsync(schema);
            retained.Select(a => a.Id).Should().BeEquivalentTo(before.Select(a => a.Id));
            if (partiallyCopied)
            {
                retained.Where(a => a.Filename != "updated.txt").Select(a => a.StoragePath)
                    .Should().BeEquivalentTo(before.Where(a => a.Filename != "photo2.jpg").Select(a => a.StoragePath));
            }
            else
            {
                retained.Select(a => a.StoragePath).Should().BeEquivalentTo(before.Select(a => a.StoragePath));
            }

            storage.Files.Keys.Should().BeEquivalentTo(retained.Select(a => a.StoragePath));

            handler.InventoryJson = ReducedInventory;
            handler.FailedDownload = null;
            handler.FailInventory = false;
            handler.BeforeDownload = null;
            storage.InvalidSize = false;
            (await service.ImportLayerAsync(ReimportRequest(schema))).Success.Should().BeTrue();
            var retried = (await ReadStoredAsync(schema)).Should().ContainSingle().Subject;
            retried.Id.Should().Be(before.Single(a => a.Filename == "photo2.jpg").Id);
            storage.Files.Keys.Should().Equal(retried.StoragePath);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportLayerAsync_HonuaAndLegacyAttachments_PreservesRowsAndReviewsUnknownProvenance(bool legacy)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("AttachmentLegacy");
        try
        {
            var storage = new ImportStorage();
            var store = await CreatePersistentStoreAsync(schema, storage);
            var handler = new AttachmentFeatureServerHandler();
            var service = CreateService(handler, store, new StubLayerPublishingService(42), schema);
            (await service.ImportLayerAsync(ReimportRequest(schema))).Success.Should().BeTrue();
            using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("manual"));
            var manual = await store.UploadAsync(42, 1, "photo1.jpg", "image/jpeg", bytes);
            if (legacy)
            {
                await using var legacyConnection = await fixture.DataSource.OpenConnectionAsync();
                await using var markLegacy = new NpgsqlCommand($"UPDATE {schema}.attachments SET attachment_origin = NULL WHERE id = $1", legacyConnection);
                markLegacy.Parameters.AddWithValue(manual.Id);
                await markLegacy.ExecuteNonQueryAsync();
            }

            // A queued file that still has a live reference must survive cleanup (including
            // compensation after a lost commit acknowledgement).
            await using (var connection = await fixture.DataSource.OpenConnectionAsync())
            await using (var queue = new NpgsqlCommand($"INSERT INTO {schema}.import_attachment_cleanup VALUES ($1, 42, 1)", connection))
            {
                queue.Parameters.AddWithValue(manual.StoragePath);
                await queue.ExecuteNonQueryAsync();
            }

            handler.InventoryJson = """{"attachmentGroups":[]}""";

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var result = await service.ImportLayerAsync(ReimportRequest(schema));
                result.Success.Should().Be(!legacy);
                result.NeedsReview.Should().Be(legacy);
                if (legacy)
                {
                    result.Warnings.Should().Contain(w => w.Contains("Untracked attachments", StringComparison.Ordinal));
                    result.FidelityDifferences.Should().Contain(d => d.Actual == "target attachment inventory requires review");
                }
                else
                {
                    result.FidelityDifferences.Should().NotContain(d => d.Code == MigrationFidelityDifferenceCodes.AttachmentsUnverified);
                }

                result.FidelityDifferences.Should().NotContain(d => d.Actual != null && d.Actual.Contains("unreadable attachment inventory", StringComparison.Ordinal));
                (await ReadStoredAsync(schema)).Should().ContainSingle().Which.Should().Be(manual);
                storage.Files.Keys.Should().Equal(manual.StoragePath);
                (await PendingCleanupAsync(schema)).Should().Be(0);
            }
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task ImportLayerAsync_ReconciliationDeleteFails_RollsBackRetirementAndRetryCompletes()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("AttachmentReconciliationFailure");
        try
        {
            var storage = new ImportStorage();
            var store = await CreatePersistentStoreAsync(schema, storage);
            var handler = new AttachmentFeatureServerHandler();
            var service = CreateService(handler, store, new StubLayerPublishingService(42), schema);
            (await service.ImportLayerAsync(ReimportRequest(schema))).Success.Should().BeTrue();
            var before = await ReadStoredAsync(schema);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using (var trigger = new NpgsqlCommand($"""
                CREATE FUNCTION {schema}.reject_retirement() RETURNS trigger LANGUAGE plpgsql AS
                $$ BEGIN RAISE EXCEPTION 'Retirement unavailable'; END $$;
                CREATE TRIGGER reject_retirement BEFORE DELETE ON {schema}.attachments
                    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_retirement();
                """, connection))
            {
                await trigger.ExecuteNonQueryAsync();
            }

            handler.InventoryJson = """{"attachmentGroups":[]}""";
            var failed = await service.ImportLayerAsync(ReimportRequest(schema));
            failed.Success.Should().BeFalse();
            failed.NeedsReview.Should().BeTrue();
            failed.Warnings.Should().Contain(w => w.Contains("reconciliation failed", StringComparison.Ordinal));
            (await ReadStoredAsync(schema)).Should().BeEquivalentTo(before);
            storage.Files.Keys.Should().BeEquivalentTo(before.Select(a => a.StoragePath));
            (await PendingCleanupAsync(schema)).Should().Be(0);

            await using (var removeTrigger = new NpgsqlCommand($"DROP TRIGGER reject_retirement ON {schema}.attachments", connection))
            {
                await removeTrigger.ExecuteNonQueryAsync();
            }

            (await service.ImportLayerAsync(ReimportRequest(schema))).Success.Should().BeTrue();
            (await ReadStoredAsync(schema)).Should().BeEmpty();
            storage.Files.Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task ImportLayerAsync_StorageCleanupFailure_QueuesCleanupAndNextImportRetriesIt()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("AttachmentCleanupRetry");
        try
        {
            var storage = new ImportStorage();
            var store = await CreatePersistentStoreAsync(schema, storage);
            var handler = new AttachmentFeatureServerHandler();
            var service = CreateService(handler, store, new StubLayerPublishingService(42), schema);
            (await service.ImportLayerAsync(ReimportRequest(schema))).Success.Should().BeTrue();

            handler.InventoryJson = ReducedInventory;
            storage.FailDeletes = true;
            (await service.ImportLayerAsync(ReimportRequest(schema))).Success.Should().BeTrue();
            (await ReadStoredAsync(schema)).Should().ContainSingle();
            (await PendingCleanupAsync(schema)).Should().Be(3);
            storage.Files.Should().HaveCount(4);

            // A newly constructed store proves cleanup is persisted, not held in a service field.
            storage.FailDeletes = false;
            store = new PostgresAttachmentStore(new FixtureConnectionProvider(fixture), storage.Mock.Object,
                NullLogger<PostgresAttachmentStore>.Instance, schema);
            service = CreateService(handler, store, new StubLayerPublishingService(42), schema);
            (await service.ImportLayerAsync(ReimportRequest(schema))).Success.Should().BeTrue();
            var current = (await ReadStoredAsync(schema)).Should().ContainSingle().Subject;
            storage.Files.Keys.Should().Equal(current.StoragePath);
            (await PendingCleanupAsync(schema)).Should().Be(0);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static GeoservicesImportRequest ReimportRequest(string schema) => new()
    {
        ServiceUrl = "https://example.com/arcgis/rest/services/Inspections/FeatureServer",
        LayerId = 0,
        TableName = "attachment_reimport",
        TargetSchema = schema,
        TargetSrid = 4326,
        BatchSize = 10,
        RequestTimeoutSeconds = 5,
        MaxRetries = 0,
        AutoPublish = true,
        ServiceName = "default",
        OverwriteExisting = true
    };

    private async Task<PostgresAttachmentStore> CreatePersistentStoreAsync(string schema, ImportStorage storage)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var create = new NpgsqlCommand($"""
            CREATE TABLE {schema}.attachments (
                id BIGSERIAL PRIMARY KEY, feature_id BIGINT NOT NULL, layer_id INT NOT NULL,
                filename TEXT NOT NULL, content_type TEXT NOT NULL, size BIGINT NOT NULL CHECK (size >= 0),
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), storage_path TEXT NOT NULL, keywords TEXT);
            """, connection);
        await create.ExecuteNonQueryAsync();
        var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "125_AddImportedAttachmentIdentity.sql"));
        await using var migrate = new NpgsqlCommand(migration.Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal), connection);
        await migrate.ExecuteNonQueryAsync();
        return new PostgresAttachmentStore(new FixtureConnectionProvider(fixture), storage.Mock.Object,
            NullLogger<PostgresAttachmentStore>.Instance, schema);
    }

    private async Task<Attachment[]> ReadStoredAsync(string schema)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT id, feature_id, filename, content_type, size, created_at, storage_path, keywords FROM {schema}.attachments WHERE layer_id = 42 ORDER BY id", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<Attachment>();
        while (await reader.ReadAsync())
        {
            result.Add(Attachment.Create(reader.GetInt64(0), reader.GetInt64(1), 42, reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4), reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return result.ToArray();
    }

    private async Task<long> PendingCleanupAsync(string schema)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {schema}.import_attachment_cleanup", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class ImportStorage
    {
        public Mock<ICloudFileStorage> Mock { get; } = new(MockBehavior.Strict);
        public Dictionary<string, byte[]> Files { get; } = [];
        public bool FailDeletes { get; set; }
        public bool InvalidSize { get; set; }

        public ImportStorage()
        {
            Mock.Setup(s => s.UploadAsync(It.IsAny<FileUploadRequest>(), It.IsAny<CancellationToken>()))
                .Returns(async (FileUploadRequest request, CancellationToken token) =>
                {
                    using var buffer = new MemoryStream();
                    await request.Content.CopyToAsync(buffer, token);
                    var path = Guid.NewGuid().ToString("N");
                    Files[path] = buffer.ToArray();
                    return UploadResult.CreateSuccess(new CloudFile
                    {
                        FileId = path,
                        StoragePath = path,
                        FileName = request.FileName,
                        ContentType = request.ContentType,
                        SizeBytes = InvalidSize ? -1 : buffer.Length,
                        UploadedAt = DateTimeOffset.UtcNow,
                        Provider = CloudStorageProvider.Local
                    });
                });
            Mock.Setup(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string path, CancellationToken _) => FailDeletes
                    ? Task.FromException<bool>(new IOException("Storage unavailable"))
                    : Task.FromResult(Files.Remove(path)));
        }
    }
}
