// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.TestKit.Helpers;

// Accepts every conditional write without comparing the expected status.
public class AcceptingInMemoryProgressStore : IUniversalProgressStore
{
    private readonly ConcurrentDictionary<string, IOperationProgress> _progress = new(StringComparer.Ordinal);

    public Task SetProgressAsync(string operationId, IOperationProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        _progress[operationId] = progress;
        return Task.CompletedTask;
    }

    public virtual Task<ProgressCompareAndSetResult> TrySetProgressAsync(OperationProgressWrite write, OperationStatus expectedStatus, CancellationToken cancellationToken = default)
    {
        _progress[write.OperationId] = write.Progress;
        return Task.FromResult(ProgressCompareAndSetResult.Updated);
    }

    public virtual Task<ProgressCompareAndSetResult> TryReplaceProgressAsync(OperationProgressWrite write, IOperationProgress? expectedProgress, CancellationToken cancellationToken = default)
    {
        _progress[write.OperationId] = write.Progress;
        return Task.FromResult(ProgressCompareAndSetResult.Updated);
    }

    public Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
        where TProgress : class, IOperationProgress
        => Task.FromResult(_progress.TryGetValue(operationId, out var p) ? p as TProgress : null);

    public Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
        => Task.FromResult(_progress.TryGetValue(operationId, out var p) ? p : null);

    public Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
    {
        _progress.TryRemove(operationId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(_progress.Keys.ToArray());

    public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType, CancellationToken cancellationToken = default)
        where TProgress : class, IOperationProgress
        => Task.FromResult<IReadOnlyList<TProgress>>(_progress.Values.OfType<TProgress>().ToArray());
}

public sealed class UnrecordedConditionalWriteProgressStore : AcceptingInMemoryProgressStore
{
    public override Task<ProgressCompareAndSetResult> TrySetProgressAsync(OperationProgressWrite write, OperationStatus expectedStatus, CancellationToken cancellationToken = default)
        => Task.FromResult(ProgressCompareAndSetResult.Updated);

    public override Task<ProgressCompareAndSetResult> TryReplaceProgressAsync(OperationProgressWrite write, IOperationProgress? expectedProgress, CancellationToken cancellationToken = default)
        => Task.FromResult(ProgressCompareAndSetResult.Updated);
}

public sealed class ComparingInMemoryProgressStore : IUniversalProgressStore
{
    private readonly ConcurrentDictionary<string, IOperationProgress> _entries = new(StringComparer.Ordinal);

    public Task<ProgressCompareAndSetResult> TryReplaceProgressAsync(OperationProgressWrite write, IOperationProgress? expectedProgress, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (!_entries.TryGetValue(write.OperationId, out var current))
            {
                if (expectedProgress is not null)
                {
                    return Task.FromResult(ProgressCompareAndSetResult.NotFound);
                }
                if (_entries.TryAdd(write.OperationId, write.Progress))
                {
                    return Task.FromResult(ProgressCompareAndSetResult.Updated);
                }
                continue;
            }

            if (!Equals(current, expectedProgress))
            {
                return Task.FromResult(ProgressCompareAndSetResult.StatusMismatch(current));
            }

            if (_entries.TryUpdate(write.OperationId, write.Progress, current))
            {
                return Task.FromResult(ProgressCompareAndSetResult.Updated);
            }
        }
    }

    public Task SetProgressAsync(
        string operationId,
        IOperationProgress progress,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        _entries[operationId] = progress;
        return Task.CompletedTask;
    }

    public Task<ProgressCompareAndSetResult> TrySetProgressAsync(OperationProgressWrite write, OperationStatus expectedStatus, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (!_entries.TryGetValue(write.OperationId, out var current))
            {
                return Task.FromResult(ProgressCompareAndSetResult.NotFound);
            }

            if (current.Status != expectedStatus)
            {
                return Task.FromResult(ProgressCompareAndSetResult.StatusMismatch(current));
            }

            if (_entries.TryUpdate(write.OperationId, write.Progress, current))
            {
                return Task.FromResult(ProgressCompareAndSetResult.Updated);
            }
        }
    }

    public Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
        where TProgress : class, IOperationProgress
    {
        if (_entries.TryGetValue(operationId, out var progress) && progress is TProgress typed)
        {
            return Task.FromResult<TProgress?>(typed);
        }

        return Task.FromResult<TProgress?>(null);
    }

    public Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
    {
        _entries.TryGetValue(operationId, out var progress);
        return Task.FromResult(progress);
    }

    public Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
    {
        _entries.TryRemove(operationId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default)
    {
        var ids = _entries
            .Where(kvp => operationType == null || kvp.Value.Type == operationType.Value)
            .Select(static kvp => kvp.Key)
            .ToArray();
        return Task.FromResult<IReadOnlyList<string>>(ids);
    }

    public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType, CancellationToken cancellationToken = default)
        where TProgress : class, IOperationProgress
    {
        var operations = _entries.Values
            .Where(progress => progress.Type == operationType)
            .OfType<TProgress>()
            .ToArray();
        return Task.FromResult<IReadOnlyList<TProgress>>(operations);
    }
}
