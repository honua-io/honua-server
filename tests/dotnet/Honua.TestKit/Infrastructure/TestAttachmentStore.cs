// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Test implementation of IAttachmentStore for unit and integration tests
/// </summary>
public sealed class TestAttachmentStore : IAttachmentStore
{
    private readonly Dictionary<(int layerId, long featureId), List<Attachment>> _attachments = new();
    private readonly Dictionary<(int layerId, long featureId, long attachmentId), byte[]> _attachmentContents = new();
    private long _nextId = 1;

    public Task<Attachment?> GetAsync(int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default)
    {
        if (!_attachments.TryGetValue((layerId, featureId), out var attachments))
            return Task.FromResult<Attachment?>(null);

        var attachment = attachments.FirstOrDefault(a => a.Id == attachmentId);
        // Since Attachment is a struct, FirstOrDefault returns default value (Id = 0) when not found
        return Task.FromResult<Attachment?>(attachment.Id == 0 ? null : attachment);
    }

    public Task<Attachment[]> ListAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        if (!_attachments.TryGetValue((layerId, featureId), out var attachments))
            return Task.FromResult(Array.Empty<Attachment>());

        return Task.FromResult(attachments.OrderByDescending(a => a.CreatedAt).ToArray());
    }

    public Task<Attachment> CreateAsync(int layerId, long featureId, Attachment attachment, CancellationToken cancellationToken = default)
    {
        var key = (layerId, featureId);
        if (!_attachments.ContainsKey(key))
            _attachments[key] = new List<Attachment>();

        var newAttachment = Attachment.Create(
            _nextId++,
            attachment.FeatureId,
            attachment.LayerId,
            attachment.Filename,
            attachment.ContentType,
            attachment.Size,
            attachment.CreatedAt,
            attachment.StoragePath,
            attachment.Keywords);

        _attachments[key].Add(newAttachment);
        return Task.FromResult(newAttachment);
    }

    public Task<Attachment> UpdateAsync(int layerId, long featureId, Attachment attachment, CancellationToken cancellationToken = default)
    {
        if (!_attachments.TryGetValue((layerId, featureId), out var attachments))
            throw new InvalidOperationException($"Attachment {attachment.Id} not found for update");

        var index = attachments.FindIndex(a => a.Id == attachment.Id);
        if (index == -1)
            throw new InvalidOperationException($"Attachment {attachment.Id} not found for update");

        attachments[index] = attachment;
        return Task.FromResult(attachment);
    }

    public async Task<Attachment> ReplaceAsync(
        int layerId,
        long featureId,
        long attachmentId,
        string filename,
        string contentType,
        Stream content,
        string? keywords = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetAsync(layerId, featureId, attachmentId, cancellationToken);
        if (!existing.HasValue)
            throw new InvalidOperationException($"Attachment {attachmentId} not found for update");

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();

        var updated = Attachment.Create(
            existing.Value.Id,
            existing.Value.FeatureId,
            existing.Value.LayerId,
            filename,
            contentType,
            bytes.LongLength,
            existing.Value.CreatedAt,
            $"test/{layerId}/{featureId}/{Guid.NewGuid()}{Path.GetExtension(filename)}",
            keywords);

        await UpdateAsync(layerId, featureId, updated, cancellationToken);
        _attachmentContents[(layerId, featureId, attachmentId)] = bytes;
        return updated;
    }

    public Task<bool> DeleteAsync(int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default)
    {
        if (!_attachments.TryGetValue((layerId, featureId), out var attachments))
            return Task.FromResult(false);

        var index = attachments.FindIndex(a => a.Id == attachmentId);
        if (index == -1)
            return Task.FromResult(false);

        attachments.RemoveAt(index);
        return Task.FromResult(true);
    }

    public async Task<Attachment> UploadAsync(int layerId, long featureId, string filename, string contentType, Stream content, string? keywords = null, CancellationToken cancellationToken = default)
    {
        // Simulate file size calculation
        var size = content.Length;

        // Validate MIME type against test allowed types
        if (!IsAllowedMimeType(contentType))
        {
            throw new ArgumentException($"File type '{contentType}' is not allowed");
        }

        // Validate file size (10MB limit for tests)
        if (size > 10 * 1024 * 1024)
        {
            throw new ArgumentException($"File size ({size:N0} bytes) exceeds maximum allowed size (10,485,760 bytes)");
        }

        var attachment = Attachment.CreateForUpload(
            id: 0, // Will be assigned by CreateAsync
            featureId: featureId,
            layerId: layerId,
            filename: filename,
            contentType: contentType,
            size: size,
            storagePath: $"test/{layerId}/{featureId}/{Guid.NewGuid()}{Path.GetExtension(filename)}",
            keywords: keywords);

        using var buffer = new MemoryStream();
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        await content.CopyToAsync(buffer, cancellationToken);
        var created = await CreateAsync(layerId, featureId, attachment, cancellationToken);
        _attachmentContents[(layerId, featureId, created.Id)] = buffer.ToArray();
        return created;
    }

    public Task<AttachmentContent?> DownloadAsync(int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default)
    {
        if (!_attachments.TryGetValue((layerId, featureId), out var attachments))
            return Task.FromResult<AttachmentContent?>(null);

        var attachment = attachments.FirstOrDefault(a => a.Id == attachmentId);
        if (attachment.Id == 0) // Default value for struct means not found
            return Task.FromResult<AttachmentContent?>(null);

        var bytes = _attachmentContents.TryGetValue((layerId, featureId, attachmentId), out var stored)
            ? stored
            : "Test file content for download"u8.ToArray();
        var content = new MemoryStream(bytes, writable: false);
        var attachmentContent = AttachmentContent.Create(attachment, content);
        return Task.FromResult<AttachmentContent?>(attachmentContent);
    }

    /// <summary>
    /// Seeds test data for integration tests
    /// </summary>
    public async Task SeedTestData(int layerId, long featureId)
    {
        var attachment1 = Attachment.CreateForUpload(
            id: 0,
            featureId: featureId,
            layerId: layerId,
            filename: "test1.txt",
            contentType: "text/plain",
            size: 100,
            storagePath: "test/path1.txt",
            keywords: "test,sample");

        var attachment2 = Attachment.CreateForUpload(
            id: 0,
            featureId: featureId,
            layerId: layerId,
            filename: "test2.jpg",
            contentType: "image/jpeg",
            size: 2048,
            storagePath: "test/path2.jpg",
            keywords: "image,test");

        await CreateAsync(layerId, featureId, attachment1);
        await CreateAsync(layerId, featureId, attachment2);
    }

    private static bool IsAllowedMimeType(string contentType)
    {
        // Test configuration: allow common file types but block executables
        var allowedTypes = new[]
        {
            "text/plain",
            "text/csv",
            "image/jpeg",
            "image/png",
            "image/gif",
            "application/pdf",
            "application/json"
        };

        return allowedTypes.Contains(contentType.ToLowerInvariant()) ||
               contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }
}
