// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.FileStorage;

/// <summary>
/// Upload progress store adapter backed by the universal progress store.
/// </summary>
internal sealed class UniversalUploadProgressStore : IUploadProgressStore
{
    private readonly IUniversalProgressStore _progressStore;

    public UniversalUploadProgressStore(IUniversalProgressStore progressStore)
    {
        _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
    }

    public Task SetProgressAsync(
        string uploadId,
        UploadProgress progress,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        ArgumentNullException.ThrowIfNull(progress);
        return _progressStore.SetProgressAsync(uploadId, progress, ttl, cancellationToken);
    }

    public Task<UploadProgress?> GetProgressAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        return _progressStore.GetProgressAsync<UploadProgress>(uploadId, cancellationToken);
    }

    public Task DeleteProgressAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        return _progressStore.DeleteProgressAsync(uploadId, cancellationToken);
    }

    public Task<IReadOnlyList<string>> GetActiveUploadIdsAsync(CancellationToken cancellationToken = default)
    {
        return _progressStore.GetActiveOperationIdsAsync(OperationType.Upload, cancellationToken);
    }

    public Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default)
    {
        return _progressStore.GetActiveOperationsAsync<UploadProgress>(OperationType.Upload, cancellationToken);
    }
}
