// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Admin.Exports;
using Moq;

namespace Honua.Server.Tests.Features.Admin.Exports;

internal sealed class FakePublicStorage
{
    private bool _failedOnce;
    private readonly string? _failOnKeyOnce;

    public FakePublicStorage(string? failOnKeyOnce = null)
    {
        _failOnKeyOnce = failOnKeyOnce;
        Storage = BuildMock();
    }

    public Mock<ICloudFileStorage> Storage { get; }
    public List<(string Key, StoredObjectCachePolicy CachePolicy)> Writes { get; } = [];
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ImmutableDictionary<string, string>> Metadata { get; } = new(StringComparer.Ordinal);

    private Mock<ICloudFileStorage> BuildMock()
    {
        var mock = new Mock<ICloudFileStorage>();
        mock.Setup(s => s.GetMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) => CreateCloudFileOrNull(key));
        mock.Setup(s => s.ListFilesAsync(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? prefix, int _, bool _, CancellationToken _) => ListByPrefix(prefix));
        mock.Setup(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) => Objects.Remove(key));
        mock.Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) => Objects.ContainsKey(key));
        mock.Setup(s => s.UploadAsync(It.IsAny<FileUploadRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileUploadRequest request, CancellationToken _) => HandleUpload(request));
        return mock;
    }

    private CloudFile? CreateCloudFileOrNull(string key)
        => Objects.TryGetValue(key, out var bytes)
            ? new CloudFile
            {
                FileId = key,
                FileName = Path.GetFileName(key),
                StoragePath = key,
                ContentType = "application/octet-stream",
                SizeBytes = bytes.Length,
                UploadedAt = DateTimeOffset.UtcNow,
                Provider = CloudStorageProvider.AwsS3,
                Metadata = Metadata.TryGetValue(key, out var meta) ? meta : ImmutableDictionary<string, string>.Empty
            }
            : null;

    private CloudFile[] ListByPrefix(string? prefix)
        => [.. Objects.Keys
            .Where(k => prefix is null || k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => CreateCloudFileOrNull(k)!)];

    private UploadResult HandleUpload(FileUploadRequest request)
    {
        var key = request.ObjectKeyOverride!;
        if (key == _failOnKeyOnce && !_failedOnce)
        {
            _failedOnce = true;
            return UploadResult.CreateFailure("Simulated crash before pointer.");
        }

        using var copy = new MemoryStream();
        request.Content.CopyTo(copy);
        Objects[key] = copy.ToArray();
        Metadata[key] = request.Metadata;
        Writes.Add((key, request.CachePolicy));
        return UploadResult.CreateSuccess(CreateCloudFileOrNull(key)!, TimeSpan.Zero);
    }
}

internal sealed class InMemoryPublicExportStateStore : IPublicExportStateStore
{
    public Dictionary<string, PromotedExportRecord> Promoted { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, PublicExportRevocationRecord> Revocations { get; } = new(StringComparer.Ordinal);

    public Task SavePromotedAsync(PromotedExportRecord record, CancellationToken cancellationToken)
    {
        Promoted[record.PublicationId] = record;
        return Task.CompletedTask;
    }

    public Task<PromotedExportRecord?> GetPromotedAsync(string publicationId, CancellationToken cancellationToken)
        => Task.FromResult(Promoted.GetValueOrDefault(publicationId));

    public Task DeletePromotedAsync(string publicationId, CancellationToken cancellationToken)
    {
        Promoted.Remove(publicationId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PromotedExportRecord>> ListPromotedAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<PromotedExportRecord>>([.. Promoted.Values]);

    public Task SaveRevocationAsync(PublicExportRevocationRecord record, CancellationToken cancellationToken)
    {
        Revocations[record.RevocationId] = record;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PublicExportRevocationRecord>> ListPendingRevocationsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<PublicExportRevocationRecord>>(
            [.. Revocations.Values.Where(r => r.State != PublicExportRevocationState.Completed)]);
}
