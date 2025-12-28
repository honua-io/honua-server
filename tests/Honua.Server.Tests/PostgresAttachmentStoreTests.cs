// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Postgres.Features.Attachments;
using Honua.Server.Tests.Infrastructure;
using Xunit.Abstractions;

namespace Honua.Server.Tests;

/// <summary>
/// Integration tests for PostgresAttachmentStore using real PostgreSQL database.
/// </summary>
[Collection("Database")]
public class PostgresAttachmentStoreTests : IAsyncLifetime
{
    private readonly DatabaseFixtureAdapter _fixture;
    private readonly ITestOutputHelper _output;
    private PostgresAttachmentStore _attachmentStore = null!;
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
        _tempStoragePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempStoragePath);

        // Create attachment store with the isolated schema
        var connectionProvider = new TestDatabaseConnectionProvider(_fixture.DataSource);
        _attachmentStore = new PostgresAttachmentStore(connectionProvider, _schemaName, _tempStoragePath);

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
        var fullPath = Path.Combine(_tempStoragePath, attachment.StoragePath);
        Assert.True(File.Exists(fullPath));

        var storedContent = await File.ReadAllBytesAsync(fullPath);
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
        var filePath = Path.Combine(_tempStoragePath, attachment.StoragePath);

        // Verify file exists before deletion
        Assert.True(File.Exists(filePath));

        // Act
        var deleted = await _attachmentStore.DeleteAsync(TestLayerId, TestFeatureId, attachment.Id);

        // Assert
        Assert.True(deleted);

        // Verify attachment is gone from database
        var retrieved = await _attachmentStore.GetAsync(TestLayerId, TestFeatureId, attachment.Id);
        Assert.Null(retrieved);

        // Verify file is deleted from filesystem
        Assert.False(File.Exists(filePath));
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

    [Fact]
    public async Task CreateAsync_WithMetadata_CreatesAttachmentRecord()
    {
        // Arrange
        var attachment = Attachment.CreateForUpload(
            id: 0,
            featureId: TestFeatureId,
            layerId: TestLayerId,
            filename: "metadata.pdf",
            contentType: "application/pdf",
            size: 1024,
            storagePath: "test/path/file.pdf",
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
    }

    private async Task<Attachment> CreateTestAttachment(string filename = "test.txt", string contentType = "text/plain", long featureId = TestFeatureId)
    {
        var content = "Test content"u8.ToArray();
        await using var stream = new MemoryStream(content);
        return await _attachmentStore.UploadAsync(TestLayerId, featureId, filename, contentType, stream, "test");
    }
}
