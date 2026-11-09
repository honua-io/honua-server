// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Abstractions;

// A repeat call with the same batch id returns the existing batch, and a child status only moves toward a terminal state.
public interface IMigrationBatchRunCatalog
{
    Task<MigrationBatchRunRecord> CreateAsync(
        MigrationBatchRunRecord record,
        string? manifestBody,
        IReadOnlyList<MigrationBatchChildRecord> children,
        CancellationToken cancellationToken = default);

    Task<MigrationBatchRunRecord?> GetAsync(
        string batchId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MigrationBatchChildRecord>> GetChildrenAsync(
        string batchId,
        CancellationToken cancellationToken = default);

    Task<string?> GetManifestBodyAsync(
        string batchId,
        CancellationToken cancellationToken = default);

    Task<MigrationBatchChildRecord?> UpdateChildAsync(
        string batchId,
        int ordinal,
        MigrationBatchChildStatus status,
        string? jobId,
        int? publishedLayerId,
        string? statusNote,
        DateTimeOffset updatedAt,
        string? fidelityVerdict = null,
        CancellationToken cancellationToken = default);

    Task<MigrationBatchRunRecord?> UpdateBatchAsync(
        string batchId,
        MigrationBatchRunStatus status,
        int succeededChildren,
        int failedChildren,
        int cancelledChildren,
        DateTimeOffset? completedAt,
        bool? relationshipsApplied,
        string? statusNote,
        string? fidelityVerdict = null,
        MigrationFidelityDifference[]? fidelityDifferences = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetActiveBatchIdsAsync(
        CancellationToken cancellationToken = default);
}
