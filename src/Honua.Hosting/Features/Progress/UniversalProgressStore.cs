// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Temporal.Domain;
using Honua.Infrastructure.Progress;

namespace Honua.Migration;

/// <summary>
/// Process-local progress store for a host without a system database (unit-level hosts and
/// tests). A host with a system database registers <c>PostgresUniversalProgressStore</c>, the
/// only shared progress store; this one never claims cross-node visibility.
/// </summary>
internal sealed partial class UniversalProgressStore : IUniversalProgressStore
{
    private const string KeyPrefix = "universal:progress:";
    private const int MaxEntries = 5000;
    private static readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _defaultTtl = TimeSpan.FromHours(24);
    private long _lastCleanupTick = Environment.TickCount64;

    public Task SetProgressAsync(string operationId, IOperationProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Store(operationId, progress, ttl ?? _defaultTtl);
        return Task.CompletedTask;
    }

    public Task<ProgressCompareAndSetResult> TrySetProgressAsync(OperationProgressWrite write, OperationStatus expectedStatus, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RejectExecutionFence(write);
        return Task.FromResult(TrySetProgress(write, expectedStatus, write.Ttl ?? _defaultTtl));
    }

    public Task<ProgressCompareAndSetResult> TryReplaceProgressAsync(OperationProgressWrite write, IOperationProgress? expectedProgress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write.Progress);
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedProgress is not null && write.Progress.Type != expectedProgress.Type)
        {
            throw new ArgumentException("A progress update cannot change the operation type.", nameof(write));
        }

        RejectExecutionFence(write);
        return Task.FromResult(TryReplaceProgress(write, expectedProgress, write.Ttl ?? _defaultTtl));
    }

    public async Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
        where TProgress : class, IOperationProgress
        => await GetProgressAsync(operationId, cancellationToken).ConfigureAwait(false) as TProgress;

    public Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetEntry($"{KeyPrefix}{operationId}")?.Progress);
    }

    public Task QuarantineProgressAsync(
        string operationId,
        OperationType operationType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RemoveEntry($"{KeyPrefix}{operationId}");
        return Task.CompletedTask;
    }

    public Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RemoveEntry($"{KeyPrefix}{operationId}");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<string>>(GetActiveOperationIds(operationType));
    }

    public async Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType, CancellationToken cancellationToken = default)
        where TProgress : class, IOperationProgress
    {
        var operationIds = await GetActiveOperationIdsAsync(operationType, cancellationToken).ConfigureAwait(false);
        var operations = new List<TProgress>();

        await foreach (var progress in ProgressStoreReader.ReadAvailableAsync(this, operationIds, cancellationToken))
        {
            if (progress is TProgress typedProgress)
            {
                operations.Add(typedProgress);
            }
        }

        return operations;
    }

    private static void RejectExecutionFence(OperationProgressWrite write)
    {
        if (write.ExecutionFence is not null)
        {
            throw new NotSupportedException("Execution-fenced progress requires durable job storage.");
        }
    }
}

internal sealed class DistributedProgressStoreAdapter<TProgress> : IDistributedProgressStore<TProgress>
    where TProgress : class, IOperationProgress
{
    private readonly IUniversalProgressStore _universalStore;

    public DistributedProgressStoreAdapter(IUniversalProgressStore universalStore)
    {
        _universalStore = universalStore;
    }

    public async Task SetProgressAsync(string jobId, TProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        await _universalStore.SetProgressAsync(jobId, (Core.Features.Infrastructure.Domain.IOperationProgress)progress, ttl, cancellationToken);
    }

    public async Task<TProgress?> GetProgressAsync(string jobId, CancellationToken cancellationToken = default)
    {
        return await _universalStore.GetProgressAsync<TProgress>(jobId, cancellationToken);
    }

    public async Task DeleteProgressAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await _universalStore.DeleteProgressAsync(jobId, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetActiveJobIdsAsync(CancellationToken cancellationToken = default)
    {
        var operationType = GetOperationTypeForProgressType<TProgress>();
        if (operationType is not { } resolvedOperationType)
        {
            return [];
        }

        var operations = await _universalStore.GetActiveOperationsAsync<TProgress>(resolvedOperationType, cancellationToken);
        return operations
            .Select(progress => progress.OperationId)
            .ToArray();
    }

    private static OperationType? GetOperationTypeForProgressType<T>()
        where T : class, IOperationProgress
    {
        // Map progress types to their corresponding operation types
        return typeof(T).Name switch
        {
            nameof(ImportProgress) => OperationType.Import,
            nameof(GeoservicesImportProgress) => OperationType.ExternalImport,
            nameof(GeoServerImportProgress) => OperationType.ExternalImport,
            nameof(TileOperationProgress) => OperationType.TileCache,
            nameof(ExportProgress) => OperationType.Export,
            nameof(PrintProgress) => OperationType.Print,
            nameof(GeoprocessingProgress) => OperationType.Geoprocessing,
            nameof(PublishingProgress) => OperationType.Publishing,
            nameof(LayerPublishJobProgress) => OperationType.Publishing,
            nameof(WorkflowProgress) => OperationType.Orchestration,
            nameof(DeploymentProgress) => OperationType.Deployment,
            _ => null
        };
    }
}

internal sealed record ProgressWrapper
{
    public required OperationType Type { get; init; }
    public required string ProgressType { get; init; }
    public required JsonElement Data { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.General)]
[JsonSerializable(typeof(ProgressWrapper))]
[JsonSerializable(typeof(ImportProgress))]
[JsonSerializable(typeof(GeoservicesImportProgress))]
[JsonSerializable(typeof(GeoServerImportProgress))]
[JsonSerializable(typeof(MigrationApplyExecutionArtifact))]
[JsonSerializable(typeof(MigrationApplyExecutionSummary))]
[JsonSerializable(typeof(MigrationApplyExecutionStepResult))]
[JsonSerializable(typeof(UploadProgress))]
[JsonSerializable(typeof(IngestProgress))]
[JsonSerializable(typeof(TileOperationProgress))]
[JsonSerializable(typeof(ExportProgress))]
[JsonSerializable(typeof(PrintProgress))]
[JsonSerializable(typeof(RasterImportProgress))]
[JsonSerializable(typeof(RasterImportPhase))]
[JsonSerializable(typeof(GeoprocessingProgress))]
[JsonSerializable(typeof(GeoprocessingWorkflowStatus))]
[JsonSerializable(typeof(GeoprocessingStageKind))]
[JsonSerializable(typeof(GeoprocessingStageStatus))]
[JsonSerializable(typeof(PublishingProgress))]
[JsonSerializable(typeof(PublishIntentStatus))]
[JsonSerializable(typeof(WorkflowProgress))]
[JsonSerializable(typeof(WorkflowRunStatus))]
[JsonSerializable(typeof(DeploymentProgress))]
[JsonSerializable(typeof(DeploymentStatus))]
[JsonSerializable(typeof(RolloutState))]
[JsonSerializable(typeof(TemporalCorrectiveJobProgress))]
[JsonSerializable(typeof(LayerPublishJobProgress))]
[JsonSerializable(typeof(OperationType))]
[JsonSerializable(typeof(OperationStatus))]
[JsonSerializable(typeof(ImportStatus))]
[JsonSerializable(typeof(GeoservicesImportStatus))]
[JsonSerializable(typeof(GeoServerImportStatus))]
[JsonSerializable(typeof(Honua.Core.Features.Tiles.PMTiles.PMTilesArtifactDescriptor))]
[JsonSerializable(typeof(Honua.Core.Features.Tiles.PMTiles.PMTilesUrlStrategy))]
internal sealed partial class UniversalProgressJsonContext : JsonSerializerContext
{
}
