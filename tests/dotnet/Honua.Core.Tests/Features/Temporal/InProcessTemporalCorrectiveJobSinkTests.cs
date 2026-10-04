// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Temporal.Domain;
using Honua.Core.Features.Temporal.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Temporal;

/// <summary>
/// Unit tests for the progress-store-backed job status of <see cref="InProcessTemporalCorrectiveJobSink"/>
/// (honua-server#1593): the returned job id resolves to Queued/Processing/terminal states, and terminal
/// transitions use compare-and-set so they never clobber a terminal state another writer recorded.
/// </summary>
public sealed class InProcessTemporalCorrectiveJobSinkTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SubmitAsync_PersistsQueuedProgress_AndRunsToCompleted()
    {
        var store = new RecordingProgressStore();
        var sink = CreateSink(store);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (jobId, status) = await sink.SubmitAsync("temporal.rollback", "svc", 0, async _ => await gate.Task);

        status.Should().Be("Queued");
        jobId.Should().StartWith("temporal-");

        var processing = await WaitForStatusAsync(store, jobId, OperationStatus.Processing);
        processing.ServiceId.Should().Be("svc");
        processing.OperationName.Should().Be("temporal.rollback");

        gate.SetResult();

        var completed = await WaitForStatusAsync(store, jobId, OperationStatus.Completed);
        completed.CompletedAt.Should().NotBeNull();
        completed.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task SubmitAsync_WhenWorkFails_RecordsFailedStatusWithError()
    {
        var store = new RecordingProgressStore();
        var sink = CreateSink(store);

        var (jobId, _) = await sink.SubmitAsync(
            "temporal.rollback", "svc", 3, _ => throw new InvalidOperationException("corrective work failed"));

        var failed = await WaitForStatusAsync(store, jobId, OperationStatus.Failed);
        failed.ErrorMessage.Should().Be("corrective work failed");
        failed.CompletedAt.Should().NotBeNull();
        failed.LayerId.Should().Be(3);
    }

    [Fact]
    public async Task SubmitAsync_WhenTerminalStateRecordedExternally_DoesNotOverwriteIt()
    {
        var store = new RecordingProgressStore();
        var sink = CreateSink(store);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (jobId, _) = await sink.SubmitAsync("temporal.rollback", "svc", 0, async _ => await gate.Task);
        await WaitForStatusAsync(store, jobId, OperationStatus.Processing);

        // Simulate another writer recording a terminal state before the sink's own terminal write.
        var current = (await store.GetProgressAsync<TemporalCorrectiveJobProgress>(jobId))!;
        await store.SetProgressAsync(
            jobId, current with { Status = OperationStatus.Cancelled, CompletedAt = DateTimeOffset.UtcNow });

        gate.SetResult();

        var rejected = await store.FirstRejectedConditionalWrite.WaitAsync(WaitTimeout);
        rejected.Outcome.Should().Be(ProgressCompareAndSetOutcome.StatusMismatch);

        var final = await store.GetProgressAsync<TemporalCorrectiveJobProgress>(jobId);
        final!.Status.Should().Be(
            OperationStatus.Cancelled,
            "the sink's terminal compare-and-set must not clobber a terminal state another writer recorded");
    }

    [Fact]
    public async Task SubmitAsync_WithoutProgressStore_StillRunsWork()
    {
        var sink = CreateSink(progressStore: null);
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (jobId, status) = await sink.SubmitAsync("temporal.rollback", "svc", 0, _ =>
        {
            ran.SetResult();
            return Task.CompletedTask;
        });

        status.Should().Be("Queued");
        jobId.Should().NotBeNullOrWhiteSpace();
        await ran.Task.WaitAsync(WaitTimeout);
    }

    private static InProcessTemporalCorrectiveJobSink CreateSink(IUniversalProgressStore? progressStore)
        => new(NullLogger<InProcessTemporalCorrectiveJobSink>.Instance, progressStore);

    private static async Task<TemporalCorrectiveJobProgress> WaitForStatusAsync(
        RecordingProgressStore store,
        string jobId,
        OperationStatus status)
    {
        var deadline = DateTime.UtcNow.Add(WaitTimeout);
        while (DateTime.UtcNow < deadline)
        {
            var progress = await store.GetProgressAsync<TemporalCorrectiveJobProgress>(jobId);
            if (progress?.Status == status)
            {
                return progress;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Job '{jobId}' did not reach status '{status}' within {WaitTimeout}.");
    }

    /// <summary>
    /// In-memory <see cref="IUniversalProgressStore"/> with atomic compare-and-set that records the first
    /// rejected conditional write so tests can await the sink's conflict handling deterministically.
    /// </summary>
    private sealed class RecordingProgressStore : IUniversalProgressStore
    {
        private readonly ConcurrentDictionary<string, IOperationProgress> _entries = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource<ProgressCompareAndSetResult> _firstRejectedConditionalWrite =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ProgressCompareAndSetResult> FirstRejectedConditionalWrite => _firstRejectedConditionalWrite.Task;

        public Task SetProgressAsync(string operationId, IOperationProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _entries[operationId] = progress;
            return Task.CompletedTask;
        }

        public Task<ProgressCompareAndSetResult> TrySetProgressAsync(
            string operationId,
            IOperationProgress progress,
            OperationStatus expectedStatus,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            while (true)
            {
                ProgressCompareAndSetResult result;
                if (!_entries.TryGetValue(operationId, out var current))
                {
                    result = ProgressCompareAndSetResult.NotFound;
                }
                else if (current.Status != expectedStatus)
                {
                    result = ProgressCompareAndSetResult.StatusMismatch(current);
                }
                else if (_entries.TryUpdate(operationId, progress, current))
                {
                    result = ProgressCompareAndSetResult.Updated;
                }
                else
                {
                    continue;
                }

                if (result.Outcome != ProgressCompareAndSetOutcome.Updated)
                {
                    _firstRejectedConditionalWrite.TrySetResult(result);
                }

                return Task.FromResult(result);
            }
        }

        public Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult(_entries.TryGetValue(operationId, out var progress) ? progress as TProgress : null);

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
            IReadOnlyList<string> ids = _entries
                .Where(kvp => operationType == null || kvp.Value.Type == operationType.Value)
                .Select(kvp => kvp.Key)
                .ToArray();
            return Task.FromResult(ids);
        }

        public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
        {
            IReadOnlyList<TProgress> operations = _entries.Values
                .Where(progress => progress.Type == operationType)
                .OfType<TProgress>()
                .ToArray();
            return Task.FromResult(operations);
        }
    }
}
