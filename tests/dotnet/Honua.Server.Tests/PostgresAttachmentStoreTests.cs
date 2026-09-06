// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Exceptions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Db.Postgres.Features.Attachments;
using Honua.FileStorage;
using Honua.Infrastructure.Security;
using Honua.Server.Tests.Infrastructure;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit.Abstractions;

namespace Honua.Server.Tests;

/// <summary>
/// Integration tests for PostgresAttachmentStore using real PostgreSQL database.
/// </summary>
[Collection("Database.CoreFeatureStore")]
public class PostgresAttachmentStoreTests : IAsyncLifetime
{
    private readonly DatabaseFixtureAdapter _fixture;
    private readonly ITestOutputHelper _output;
    private PostgresAttachmentStore _attachmentStore = null!;
    private LocalFileStorage _fileStorage = null!;
    private string _schemaName = null!;
    private string _tempStoragePath = null!;
    private const int TestLayerId = 1;
    private const long TestFeatureId = 123;

    public PostgresAttachmentStoreTests(DatabaseFixtureAdapter fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _schemaName = await _fixture.CreateIsolatedSchemaAsync(nameof(PostgresAttachmentStoreTests));
        // Guid.NewGuid().ToString() is never rooted, so GetTempPath() is never dropped.
        _tempStoragePath = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempStoragePath);

        var progressStore = Substitute.For<IUploadProgressStore>();
        _fileStorage = new LocalFileStorage(
            Options.Create(new LocalStorageOptions
            {
                BasePath = _tempStoragePath,
                CreateDirectoryIfNotExists = true
            }),
            NullLogger<LocalFileStorage>.Instance,
            progressStore);

        // Create attachment store with the isolated schema
        var connectionProvider = new TestDatabaseConnectionProvider(_fixture.DataSource);
        _attachmentStore = new PostgresAttachmentStore(
            connectionProvider,
            _fileStorage,
            NullLogger<PostgresAttachmentStore>.Instance,
            _schemaName);

        // Create test table structure
        await _fixture.ExecuteAsync($"""
            CREATE TABLE {_schemaName}.attachments (
                id BIGSERIAL PRIMARY KEY,
                feature_id BIGINT NOT NULL,
                layer_id INT NOT NULL,
                filename TEXT NOT NULL,
                content_type TEXT NOT NULL,
                size BIGINT NOT NULL CHECK (size >= 0),
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                storage_path TEXT NOT NULL,
                keywords TEXT
            );

            CREATE INDEX idx_attachments_feature_layer ON {_schemaName}.attachments(layer_id, feature_id);
            CREATE INDEX idx_attachments_created_at ON {_schemaName}.attachments(created_at);
            """);

        _output.WriteLine($"Created isolated schema: {_schemaName}");
        _output.WriteLine($"Created temp storage: {_tempStoragePath}");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropSchemaAsync(_schemaName);

        // Clean up temporary files
        if (Directory.Exists(_tempStoragePath))
        {
            Directory.Delete(_tempStoragePath, recursive: true);
        }
    }

    [Fact]
    public async Task UploadAsync_WithValidFile_CreatesAttachmentAndStoresFile()
    {
        // Arrange
        const string filename = "test.txt";
        const string contentType = "text/plain";
        const string keywords = "test,sample";
        var content = "Hello, World!"u8.ToArray();

        // Act
        await using var stream = new MemoryStream(content);
        var attachment = await _attachmentStore.UploadAsync(
            TestLayerId, TestFeatureId, filename, contentType, stream, keywords);

        // Assert
        Assert.True(attachment.Id > 0);
        Assert.Equal(TestFeatureId, attachment.FeatureId);
        Assert.Equal(TestLayerId, attachment.LayerId);
        Assert.Equal(filename, attachment.Filename);
        Assert.Equal(contentType, attachment.ContentType);
        Assert.Equal(content.Length, attachment.Size);
        Assert.Equal(keywords, attachment.Keywords);
        Assert.NotEmpty(attachment.StoragePath);

        // Verify file was stored
        Assert.True(await _fileStorage.ExistsAsync(attachment.StoragePath));

        var storedContent = await _fileStorage.DownloadBytesAsync(attachment.StoragePath);
        Assert.NotNull(storedContent);
        Assert.Equal(content, storedContent);
    }

    [Fact]
    public async Task GetAsync_ExistingAttachment_ReturnsAttachment()
    {
        // Arrange
        var created = await CreateTestAttachment();

        // Act
        var retrieved = await _attachmentStore.GetAsync(TestLayerId, TestFeatureId, created.Id);

        // Assert
        Assert.NotNull(retrieved);
        if (retrieved is null)
        {
            throw new InvalidOperationException("GetAsync should have returned an attachment.");
        }

        Assert.Equal(created.Id, retrieved.Value.Id);
        Assert.Equal(created.Filename, retrieved.Value.Filename);
        Assert.Equal(created.ContentType, retrieved.Value.ContentType);
        Assert.Equal(created.Size, retrieved.Value.Size);
        Assert.Equal(created.Keywords, retrieved.Value.Keywords);
    }

    [Fact]
    public async Task GetAsync_NonExistentAttachment_ReturnsNull()
    {
        // Act
        var result = await _attachmentStore.GetAsync(TestLayerId, TestFeatureId, 99999);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ListAsync_WithAttachments_ReturnsAllAttachmentsForFeature()
    {
        // Arrange
        var attachment1 = await CreateTestAttachment("file1.txt", "text/plain");
        var attachment2 = await CreateTestAttachment("file2.jpg", "image/jpeg");
        await CreateTestAttachment("file3.txt", "text/plain", featureId: 456); // Different feature

        // Act
        var attachments = await _attachmentStore.ListAsync(TestLayerId, TestFeatureId);

        // Assert
        Assert.Equal(2, attachments.Length);
        Assert.Contains(attachments, a => a.Id == attachment1.Id);
        Assert.Contains(attachments, a => a.Id == attachment2.Id);
        Assert.All(attachments, a => Assert.Equal(TestFeatureId, a.FeatureId));
    }

    [Fact]
    public async Task ListAsync_NoAttachments_ReturnsEmptyArray()
    {
        // Act
        var attachments = await _attachmentStore.ListAsync(TestLayerId, 999);

        // Assert
        Assert.Empty(attachments);
    }

    [Fact]
    public async Task UpdateAsync_ExistingAttachment_UpdatesMetadata()
    {
        // Arrange
        var original = await CreateTestAttachment();
        var updated = Attachment.Create(
            original.Id,
            original.FeatureId,
            original.LayerId,
            "updated.txt", // Changed filename
            "application/octet-stream", // Changed content type
            original.Size,
            original.CreatedAt,
            original.StoragePath,
            "updated,keywords"); // Changed keywords

        // Act
        var result = await _attachmentStore.UpdateAsync(TestLayerId, TestFeatureId, updated);

        // Assert
        Assert.Equal(updated.Id, result.Id);
        Assert.Equal(updated.Filename, result.Filename);
        Assert.Equal(updated.ContentType, result.ContentType);
        Assert.Equal(updated.Keywords, result.Keywords);

        // Verify original values that shouldn't change
        Assert.Equal(original.Size, result.Size);
        Assert.Equal(original.StoragePath, result.StoragePath);
        Assert.Equal(original.CreatedAt, result.CreatedAt);

        // #4404: asserting only on the returned value proves the method's return path, not
        // that the row was written. Re-read it.
        var persisted = await _attachmentStore.GetAsync(TestLayerId, TestFeatureId, original.Id);
        Assert.NotNull(persisted);
        Assert.Equal(updated.Filename, persisted!.Value.Filename);
        Assert.Equal(updated.ContentType, persisted.Value.ContentType);
        Assert.Equal(updated.Keywords, persisted.Value.Keywords);
        Assert.Equal(original.Size, persisted.Value.Size);
        Assert.Equal(original.StoragePath, persisted.Value.StoragePath);
    }

    [Theory]
    [InlineData("new keywords")]
    [InlineData(null)]
    public async Task UpdateAttachmentAsync_KeywordsReadBeforeReplacement_PreservesReplacementContent(string? keywords)
    {
        var original = await CreateTestAttachment();
        const string replacementText = "replacement content survives the keywords update";
        Attachment replacement = default;
        var interleavedStore = Substitute.For<IAttachmentStore>();
        interleavedStore.GetAsync(TestLayerId, TestFeatureId, original.Id, Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var snapshot = await _attachmentStore.GetAsync(TestLayerId, TestFeatureId, original.Id);
                using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(replacementText));
                replacement = await _attachmentStore.ReplaceAsync(
                    TestLayerId, TestFeatureId, original.Id, "replacement.txt", "text/plain", content, "replacement keywords");
                return snapshot;
            });
        interleavedStore.UpdateAsync(TestLayerId, TestFeatureId, Arg.Any<Attachment>(), Arg.Any<CancellationToken>())
            .Returns(call => _attachmentStore.UpdateAsync(TestLayerId, TestFeatureId, call.Arg<Attachment>()));

        interleavedStore.UpdateKeywordsAsync(TestLayerId, TestFeatureId, original.Id, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => _attachmentStore.UpdateKeywordsAsync(TestLayerId, TestFeatureId, original.Id, call.Arg<string?>()));

        var response = await AttachmentHandler.UpdateAttachmentAsync(
            new DefaultHttpContext(), TestLayerId, TestFeatureId, original.Id, null, keywords,
            interleavedStore, new AttachmentLimits(), new FileUploadSecurityOptions(),
            NullLogger<AttachmentOperations>.Instance, CancellationToken.None);

        Assert.True(Assert.IsType<Ok<UpdateAttachmentResponse>>(response).Value!.UpdateAttachmentResult.Success);
        var current = await _attachmentStore.GetAsync(TestLayerId, TestFeatureId, original.Id);
        Assert.True(current.HasValue);
        Assert.Equal(replacement.StoragePath, current.Value.StoragePath);
        Assert.Equal(replacement.Filename, current.Value.Filename);
        Assert.Equal(replacement.ContentType, current.Value.ContentType);
        Assert.Equal(replacement.Size, current.Value.Size);
        Assert.Equal(keywords, current.Value.Keywords);
        Assert.False(await _fileStorage.ExistsAsync(original.StoragePath));
        var download = await _attachmentStore.DownloadAsync(TestLayerId, TestFeatureId, original.Id);
        Assert.True(download.HasValue);
        using var reader = new StreamReader(download.Value.Content);
        Assert.Equal(replacementText, await reader.ReadToEndAsync());
    }

    [Theory]
    [InlineData(2, TestFeatureId)]
    [InlineData(TestLayerId, 999)]
    public async Task UpdateKeywordsAsync_WrongOwner_DoesNotChangeAttachment(int layerId, long featureId)
    {
        var original = await CreateTestAttachment();

        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            _attachmentStore.UpdateKeywordsAsync(layerId, featureId, original.Id, "wrong owner"));

        Assert.Equal(original, await _attachmentStore.GetAsync(TestLayerId, TestFeatureId, original.Id));
    }

    [Fact]
    public async Task UpdateKeywordsAsync_DeletedAttachment_DoesNotRecreateRecord()
    {
        var original = await CreateTestAttachment();
        Assert.True(await _attachmentStore.DeleteAsync(TestLayerId, TestFeatureId, original.Id));

        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            _attachmentStore.UpdateKeywordsAsync(TestLayerId, TestFeatureId, original.Id, "new keywords"));

        Assert.Null(await _attachmentStore.GetAsync(TestLayerId, TestFeatureId, original.Id));
    }

    [Fact]
    public async Task UpdateAsync_NonExistentAttachment_ThrowsException()
    {
        // Arrange
        var attachment = Attachment.Create(99999, TestFeatureId, TestLayerId, "test.txt", "text/plain", 100, DateTime.UtcNow, "path", null);

        // Act & Assert
        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            _attachmentStore.UpdateAsync(TestLayerId, TestFeatureId, attachment));
    }

    [Fact]
    public async Task DeleteAsync_ExistingAttachment_DeletesAttachmentAndFile()
    {
        // Arrange
        var attachment = await CreateTestAttachment();

        // Verify file exists before deletion
        Assert.True(await _fileStorage.ExistsAsync(attachment.StoragePath));

        // Act
        var deleted = await _attachmentStore.DeleteAsync(TestLayerId, TestFeatureId, attachment.Id);

        // Assert
        Assert.True(deleted);

        // Verify attachment is gone from database
        var retrieved = await _attachmentStore.GetAsync(TestLayerId, TestFeatureId, attachment.Id);
        Assert.Null(retrieved);

        // Verify file is deleted from filesystem
        Assert.False(await _fileStorage.ExistsAsync(attachment.StoragePath));
    }

    [Fact]
    public async Task DeleteAsync_NonExistentAttachment_ReturnsFalse()
    {
        // Act
        var deleted = await _attachmentStore.DeleteAsync(TestLayerId, TestFeatureId, 99999);

        // Assert
        Assert.False(deleted);
    }

    [Fact]
    public async Task DownloadAsync_ExistingAttachment_ReturnsContentAndMetadata()
    {
        // Arrange
        var attachment = await CreateTestAttachment();

        // Act
        var result = await _attachmentStore.DownloadAsync(TestLayerId, TestFeatureId, attachment.Id);

        // Assert
        Assert.NotNull(result);
        if (result is null)
        {
            throw new InvalidOperationException("DownloadAsync should have returned attachment content.");
        }

        Assert.Equal(attachment.Id, result.Value.Attachment.Id);
        Assert.Equal(attachment.Filename, result.Value.Attachment.Filename);

        // Verify content
        using var content = result.Value.Content;
        using var reader = new StreamReader(content);
        var text = await reader.ReadToEndAsync();
        Assert.Equal("Test content", text);
    }

    [Fact]
    public async Task DownloadAsync_NonExistentAttachment_ReturnsNull()
    {
        // Act
        var result = await _attachmentStore.DownloadAsync(TestLayerId, TestFeatureId, 99999);

        // Assert
        Assert.Null(result);
    }

    /// <summary>
    /// <c>CreateAsync</c> is the metadata half of the two-step write, so it is expected to
    /// succeed for a storage path that no object backs. The name now says so: this is not
    /// evidence that an attachment round-trips, and the assertions below pin the dangling
    /// state explicitly so nobody reads it as one (honua-server#4404).
    /// </summary>
    [Fact]
    public async Task CreateAsync_WithMetadataOnly_CreatesRowPointingAtNoStoredObject()
    {
        // Arrange
        var attachment = Attachment.CreateForUpload(
            id: 0,
            featureId: TestFeatureId,
            layerId: TestLayerId,
            filename: "metadata.pdf",
            contentType: "application/pdf",
            size: 1024,
            storagePath: Guid.NewGuid().ToString("N"),
            keywords: "metadata,test");

        // Act
        var created = await _attachmentStore.CreateAsync(TestLayerId, TestFeatureId, attachment);

        // Assert
        Assert.True(created.Id > 0);
        Assert.Equal(attachment.Filename, created.Filename);
        Assert.Equal(attachment.ContentType, created.ContentType);
        Assert.Equal(attachment.Size, created.Size);
        Assert.Equal(attachment.StoragePath, created.StoragePath);
        Assert.Equal(attachment.Keywords, created.Keywords);

        // The row is deliberately dangling: no object was ever uploaded for it. Pin that,
        // so this test cannot be miscounted as round-trip evidence.
        Assert.False(await _fileStorage.ExistsAsync(created.StoragePath));
        var download = await _attachmentStore.DownloadAsync(TestLayerId, TestFeatureId, created.Id);
        Assert.Null(download);
    }

    /// <summary>
    /// The upload path writes the object first and the metadata row second, with no shared
    /// transaction. When the insert fails the compensating delete must remove the object it
    /// already wrote, or the deployment accumulates unreachable blobs nobody can enumerate.
    /// Before this test every compensating <c>catch</c> block in the store had zero coverage
    /// (honua-server#4404).
    /// </summary>
    [Fact]
    public async Task UploadAsync_WhenMetadataInsertFails_RemovesTheObjectItAlreadyUploaded()
    {
        // A store pointed at a schema with no attachments table: the object upload succeeds
        // against the shared file storage and only the INSERT fails, which is exactly the
        // window the compensating delete exists for.
        var brokenStore = new PostgresAttachmentStore(
            new TestDatabaseConnectionProvider(_fixture.DataSource),
            _fileStorage,
            NullLogger<PostgresAttachmentStore>.Instance,
            schemaName: _schemaName + "_absent");

        var objectsBefore = await ListStoredObjectIdsAsync();

        await using var stream = new MemoryStream("orphan candidate"u8.ToArray());
        await Assert.ThrowsAnyAsync<Exception>(() => brokenStore.UploadAsync(
            TestLayerId, TestFeatureId, "orphan.txt", "text/plain", stream));

        var objectsAfter = await ListStoredObjectIdsAsync();
        Assert.Equal(objectsBefore, objectsAfter);

        // And no row was created in the real table either.
        var rows = await _attachmentStore.ListAsync(TestLayerId, TestFeatureId);
        Assert.DoesNotContain(rows, attachment => attachment.Filename == "orphan.txt");
    }

    /// <summary>
    /// When the compensating delete fails too, the object is live and no row will ever
    /// reference it. That divergence must be recorded so an operator can enumerate and
    /// reconcile it — previously it was swallowed into a warning log line
    /// (honua-server#4404).
    /// </summary>
    [Fact]
    public async Task UploadAsync_WhenInsertAndCompensatingDeleteBothFail_RecordsAnOrphan()
    {
        var ledger = new RecordingOrphanLedger();
        var storage = new DeleteFailingFileStorage(_fileStorage);
        var brokenStore = new PostgresAttachmentStore(
            new TestDatabaseConnectionProvider(_fixture.DataSource),
            storage,
            NullLogger<PostgresAttachmentStore>.Instance,
            schemaName: _schemaName + "_absent",
            orphanLedger: ledger);

        await using var stream = new MemoryStream("unreachable object"u8.ToArray());
        await Assert.ThrowsAnyAsync<Exception>(() => brokenStore.UploadAsync(
            TestLayerId, TestFeatureId, "unreachable.txt", "text/plain", stream));

        var orphan = Assert.Single(ledger.Orphans);
        Assert.Equal(AttachmentOrphanKind.ObjectWithoutMetadata, orphan.Kind);
        Assert.Equal(TestLayerId, orphan.LayerId);
        Assert.Equal(TestFeatureId, orphan.FeatureId);
        Assert.NotEmpty(orphan.StoragePath);

        // The record must name the object that actually leaked, so reconciliation is possible.
        Assert.Contains(orphan.StoragePath, storage.AttemptedDeletes);
        Assert.True(await _fileStorage.ExistsAsync(orphan.StoragePath));

        // Clean up the object the store could not.
        await _fileStorage.DeleteAsync(orphan.StoragePath);
    }

    /// <summary>
    /// <c>DeleteAsync</c> removes the committed row first and then the object. A storage
    /// failure after that commit leaves an object nothing references; the caller still gets
    /// <c>true</c> (the row really is gone), so the leak has to be surfaced through the
    /// ledger rather than only logged (honua-server#4404).
    /// </summary>
    [Fact]
    public async Task DeleteAsync_WhenStorageDeleteFails_SurfacesTheUndeletedObject()
    {
        var attachment = await CreateTestAttachment("delete-failure.txt");
        Assert.True(await _fileStorage.ExistsAsync(attachment.StoragePath));

        var ledger = new RecordingOrphanLedger();
        var storage = new DeleteFailingFileStorage(_fileStorage);
        var store = new PostgresAttachmentStore(
            new TestDatabaseConnectionProvider(_fixture.DataSource),
            storage,
            NullLogger<PostgresAttachmentStore>.Instance,
            schemaName: _schemaName,
            orphanLedger: ledger);

        var deleted = await store.DeleteAsync(TestLayerId, TestFeatureId, attachment.Id);

        Assert.True(deleted);
        Assert.Null(await _attachmentStore.GetAsync(TestLayerId, TestFeatureId, attachment.Id));

        var orphan = Assert.Single(ledger.Orphans);
        Assert.Equal(AttachmentOrphanKind.UndeletedObject, orphan.Kind);
        Assert.Equal(attachment.StoragePath, orphan.StoragePath);
        Assert.Equal(TestLayerId, orphan.LayerId);
        Assert.Equal(TestFeatureId, orphan.FeatureId);
        Assert.True(await _fileStorage.ExistsAsync(attachment.StoragePath));

        await _fileStorage.DeleteAsync(attachment.StoragePath);
    }

    /// <summary>
    /// A successful delete must not report an orphan. Without this the ledger assertions
    /// above would also pass for an implementation that recorded on every delete.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_WhenStorageDeleteSucceeds_RecordsNoOrphan()
    {
        var ledger = new RecordingOrphanLedger();
        var store = new PostgresAttachmentStore(
            new TestDatabaseConnectionProvider(_fixture.DataSource),
            _fileStorage,
            NullLogger<PostgresAttachmentStore>.Instance,
            schemaName: _schemaName,
            orphanLedger: ledger);

        var attachment = await CreateTestAttachment("clean-delete.txt");

        Assert.True(await store.DeleteAsync(TestLayerId, TestFeatureId, attachment.Id));

        Assert.Empty(ledger.Orphans);
        Assert.False(await _fileStorage.ExistsAsync(attachment.StoragePath));
    }

    private async Task<HashSet<string>> ListStoredObjectIdsAsync()
    {
        var files = await _fileStorage.ListFilesAsync();
        return files.Select(file => file.FileId).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Captures every orphan the store reports, for assertion.</summary>
    private sealed class RecordingOrphanLedger : IAttachmentOrphanLedger
    {
        private readonly List<AttachmentOrphan> _orphans = [];

        public IReadOnlyList<AttachmentOrphan> Orphans
        {
            get
            {
                lock (_orphans)
                {
                    return _orphans.ToArray();
                }
            }
        }

        public ValueTask RecordAsync(AttachmentOrphan orphan, CancellationToken cancellationToken = default)
        {
            lock (_orphans)
            {
                _orphans.Add(orphan);
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Passes every call through to the real storage except <c>DeleteAsync</c>, which throws.
    /// Models the storage outage that turns a compensating delete into a leak.
    /// </summary>
    private sealed class DeleteFailingFileStorage(ICloudFileStorage inner) : ICloudFileStorage
    {
        private readonly List<string> _attemptedDeletes = [];

        public IReadOnlyList<string> AttemptedDeletes
        {
            get
            {
                lock (_attemptedDeletes)
                {
                    return _attemptedDeletes.ToArray();
                }
            }
        }

        public Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default)
        {
            lock (_attemptedDeletes)
            {
                _attemptedDeletes.Add(fileId);
            }

            throw new IOException($"Simulated storage outage deleting '{fileId}'.");
        }

        public Task<UploadResult> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
            => inner.UploadAsync(request, cancellationToken);

        public Task<UploadResult> UploadAsync(ByteArrayUploadRequest request, CancellationToken cancellationToken = default)
            => inner.UploadAsync(request, cancellationToken);

        public Task<UploadProgress?> GetUploadProgressAsync(string uploadId, CancellationToken cancellationToken = default)
            => inner.GetUploadProgressAsync(uploadId, cancellationToken);

        public Task<bool> CancelUploadAsync(string uploadId, CancellationToken cancellationToken = default)
            => inner.CancelUploadAsync(uploadId, cancellationToken);

        public Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default)
            => inner.GetActiveUploadsAsync(cancellationToken);

        public Task<Stream?> DownloadAsync(string fileId, CancellationToken cancellationToken = default)
            => inner.DownloadAsync(fileId, cancellationToken);

        public Task<byte[]?> DownloadBytesAsync(string fileId, CancellationToken cancellationToken = default)
            => inner.DownloadBytesAsync(fileId, cancellationToken);

        public Task<BatchUploadResult> UploadBatchAsync(BatchUploadRequest request, CancellationToken cancellationToken = default)
            => inner.UploadBatchAsync(request, cancellationToken);

        public Task<int> DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default)
            => inner.DeleteBatchAsync(batchId, cancellationToken);

        public Task<CloudFile?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
            => inner.GetMetadataAsync(fileId, cancellationToken);

        public Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default)
            => inner.ExistsAsync(fileId, cancellationToken);

        public Task<IReadOnlyList<CloudFile>> ListFilesAsync(
            string? folder = null,
            int maxResults = 1000,
            bool includeMetadata = true,
            CancellationToken cancellationToken = default)
            => inner.ListFilesAsync(folder, maxResults, includeMetadata, cancellationToken);

        public Task<string?> GetPresignedUrlAsync(
            string fileId,
            TimeSpan? expiresIn = null,
            CancellationToken cancellationToken = default)
            => inner.GetPresignedUrlAsync(fileId, expiresIn, cancellationToken);

        public Task<(string Url, string FileId)?> GetPresignedUploadUrlAsync(
            string fileName,
            string contentType,
            TimeSpan? expiresIn = null,
            string? folder = null,
            CancellationToken cancellationToken = default)
            => inner.GetPresignedUploadUrlAsync(fileName, contentType, expiresIn, folder, cancellationToken);

        public Task<int> CleanupExpiredFilesAsync(CancellationToken cancellationToken = default)
            => inner.CleanupExpiredFilesAsync(cancellationToken);
    }

    private async Task<Attachment> CreateTestAttachment(string filename = "test.txt", string contentType = "text/plain", long featureId = TestFeatureId)
    {
        var content = "Test content"u8.ToArray();
        await using var stream = new MemoryStream(content);
        return await _attachmentStore.UploadAsync(TestLayerId, featureId, filename, contentType, stream, "test");
    }
}
