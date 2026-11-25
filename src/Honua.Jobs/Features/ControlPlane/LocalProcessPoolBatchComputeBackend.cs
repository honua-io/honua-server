// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Subprocesses;

namespace Honua.ControlPlane;

internal sealed partial class LocalProcessPoolBatchComputeBackend : IBatchComputeBackend, IDisposable
{
    internal const string AdapterBackendName = "honua-local-process";

    internal const string PendingSubmissionMarkerPrefix = "local-process-pending:";

    internal const string LaunchedMarkerPrefix = "local-process:";

    private const int MaxTailLines = 50;

    private const int MaxRetainedUnobservedOutcomes = 10_000;

    private static readonly TimeSpan DefaultTerminalRetentionTtl = TimeSpan.FromMinutes(5);

    private BatchComputeBackendCapabilities CreateCapabilitiesSnapshot()
    {
        var configuredMaximum = _options.WorkerContracts
            .Where(contract => !string.IsNullOrWhiteSpace(contract.Executable))
            .Where(contract => contract.ArgumentPrefix.Count > 0 || contract.IsSelfContained)
            .Select(contract => Math.Max(1, contract.MaxSupportedContractVersion))
            .DefaultIfEmpty(1)
            .Max();
        return new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true,
            SupportsProgressPolling = true,
            // The reconciler re-queues a Failed job when the backend supports retry; that is exactly how a
            // process lost to a host restart recovers (a fresh launch on the next attempt).
            SupportsRetry = true,
            SupportsLogStreaming = false,
            SupportsArtifactStaging = false,
            MaxSupportedContractVersion = Math.Max(1, configuredMaximum)
        };
    }

    private readonly ILogger<LocalProcessPoolBatchComputeBackend> _logger;
    private readonly LocalProcessPoolOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<string, ProcessExecution> _executions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TerminalOutcome> _unobservedOutcomes = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _disposeCts = new();
    private volatile bool _disposed;

    internal TimeSpan TerminalRetentionTtl { get; init; } = DefaultTerminalRetentionTtl;

    internal int TrackedExecutionCount => _executions.Count;

    public LocalProcessPoolBatchComputeBackend(
        IOptions<LocalProcessPoolOptions> options,
        ILogger<LocalProcessPoolBatchComputeBackend> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
        var maxConcurrent = Math.Max(1, _options.MaxConcurrentProcesses);
        _slots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public string BackendName => AdapterBackendName;

    public BatchComputeTargetKind TargetKind => BatchComputeTargetKind.LocalProcess;

    public Task<BatchComputeBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateCapabilitiesSnapshot());

    public Task<BatchComputeSubmissionResult> StartAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        EvictExpiredTerminalExecutions();

        string executable;
        try
        {
            executable = ResolveExecutable(job.Spec.Parameters);
            ValidateLaunchParameters(job);
        }
        catch (InvalidOperationException ex)
        {
            Log.LaunchRejected(_logger, job.OperationId, ex.Message);
            return Task.FromResult(new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Failed,
                Message = ex.Message
            });
        }

        var contractFailure = ValidateExecutableContract(job, executable);
        if (contractFailure is not null)
        {
            Log.LaunchRejected(_logger, job.OperationId, contractFailure);
            return Task.FromResult(new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Failed,
                Message = contractFailure
            });
        }

        // Non-blocking admission: never hold the reconciliation lease waiting for a slot. When the pool
        // is saturated, defer with a pending marker so a later reconcile launches the process.
        if (!_slots.Wait(0, CancellationToken.None))
        {
            Log.PoolSaturated(_logger, job.OperationId, _options.MaxConcurrentProcesses);
            return Task.FromResult(new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Provisioning,
                ProviderOperationId = PendingSubmissionMarkerPrefix + job.OperationId,
                Message = $"Waiting for a local process pool slot (max {_options.MaxConcurrentProcesses} concurrent)."
            });
        }

        try
        {
            // StartAsync receives the record before the submission is counted.
            var providerId = Launch(job, executable, executionAttempt: job.AttemptCount + 1);
            return Task.FromResult(new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Running,
                ProviderOperationId = providerId,
                Message = $"Launched local process '{executable}' for job '{job.OperationId}'."
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _slots.Release();
            Log.LaunchFailed(_logger, job.OperationId, ex.Message);
            return Task.FromResult(new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Failed,
                Message = "Local process launch failed."
            });
        }
    }

    public Task<BatchComputeObservation> ObserveAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        EvictExpiredTerminalExecutions();

        if (_executions.TryGetValue(job.OperationId, out var execution))
        {
            return Task.FromResult(ObserveTrackedExecution(job, execution));
        }

        // Preserve terminal outcomes across delayed reconciliation after scratch/process cleanup.
        if (_unobservedOutcomes.TryRemove(job.OperationId, out var preservedOutcome))
        {
            return Task.FromResult(CreateObservation(job, preservedOutcome.Status, preservedOutcome.Message));
        }

        return Task.FromResult(ObserveUntrackedJob(job));
    }

    private BatchComputeObservation ObserveTrackedExecution(ExecutionJobRecord job, ProcessExecution execution)
    {
        var snapshot = execution.Snapshot();
        if (snapshot.Status.IsTerminal())
        {
            execution.MarkObserved();
            EvictExecution(job.OperationId, execution);
            _unobservedOutcomes.TryRemove(job.OperationId, out _);
        }

        return CreateObservation(job, snapshot.Status, snapshot.Message);
    }

    private BatchComputeObservation ObserveUntrackedJob(ExecutionJobRecord job)
    {
        if (IsPendingMarker(job.ProviderOperationId))
        {
            return TryLaunchPending(job);
        }

        if (job.Status.IsTerminal())
        {
            return CreateObservation(job, job.Status, "Local process already reached a terminal state.");
        }

        Log.ProcessLost(_logger, job.OperationId, job.ProviderOperationId ?? "<none>");
        return new BatchComputeObservation
        {
            Status = ExecutionJobStatus.Failed,
            ProviderOperationId = job.ProviderOperationId,
            Message = "Local process is no longer tracked (host restart?); the job will be retried."
        };
    }

    private static BatchComputeObservation CreateObservation(
        ExecutionJobRecord job,
        ExecutionJobStatus status,
        string? message)
        => new()
        {
            Status = status,
            ProviderOperationId = job.ProviderOperationId,
            PercentComplete = status == ExecutionJobStatus.Succeeded ? 100 : job.PercentComplete,
            Message = message
        };

    public Task<BatchComputeObservation> CancelAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        EvictExpiredTerminalExecutions();

        if (_executions.TryGetValue(job.OperationId, out var execution))
        {
            execution.RequestCancel();
            var snapshot = execution.Snapshot();
            var status = snapshot.Status == ExecutionJobStatus.Running
                ? ExecutionJobStatus.Cancelled
                : snapshot.Status;
            return Task.FromResult(new BatchComputeObservation
            {
                Status = status,
                ProviderOperationId = job.ProviderOperationId,
                PercentComplete = job.PercentComplete,
                Message = "Local process cancellation requested."
            });
        }

        // Pending (never launched) or lost — nothing to kill; treat as cancelled.
        return Task.FromResult(new BatchComputeObservation
        {
            Status = ExecutionJobStatus.Cancelled,
            ProviderOperationId = job.ProviderOperationId,
            Message = IsPendingMarker(job.ProviderOperationId)
                ? "Local process was cancelled before it acquired a pool slot."
                : "Local process was not tracked; treating cancellation as completed."
        });
    }

    private BatchComputeObservation TryLaunchPending(ExecutionJobRecord job)
    {
        string executable;
        try
        {
            executable = ResolveExecutable(job.Spec.Parameters);
            ValidateLaunchParameters(job);
        }
        catch (InvalidOperationException ex)
        {
            Log.LaunchRejected(_logger, job.OperationId, ex.Message);
            return new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Failed,
                ProviderOperationId = job.ProviderOperationId,
                Message = ex.Message
            };
        }

        var contractFailure = ValidateExecutableContract(job, executable);
        if (contractFailure is not null)
        {
            Log.LaunchRejected(_logger, job.OperationId, contractFailure);
            return new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Failed,
                ProviderOperationId = job.ProviderOperationId,
                Message = contractFailure
            };
        }

        if (!_slots.Wait(0, CancellationToken.None))
        {
            return new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Provisioning,
                ProviderOperationId = job.ProviderOperationId,
                Message = $"Waiting for a local process pool slot (max {_options.MaxConcurrentProcesses} concurrent)."
            };
        }

        try
        {
            // The deferred launch runs after the submission was counted on the durable record.
            var providerId = Launch(job, executable, executionAttempt: Math.Max(1, job.AttemptCount));
            return new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Running,
                ProviderOperationId = providerId,
                Message = $"Launched local process '{executable}' for job '{job.OperationId}'."
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _slots.Release();
            Log.LaunchFailed(_logger, job.OperationId, ex.Message);
            return new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Failed,
                ProviderOperationId = job.ProviderOperationId,
                Message = "Local process launch failed."
            };
        }
    }

    private string Launch(ExecutionJobRecord job, string executable, int executionAttempt)
    {
        _unobservedOutcomes.TryRemove(job.OperationId, out _);
        var (workingDirectory, ownedScratch) = ResolveWorkingDirectory(job);
        var ownedScratchDirectory = ownedScratch ? workingDirectory : null;

        ProcessStartInfo startInfo;
        try
        {
            startInfo = PrepareStartInfo(new LaunchContext(job, executable, executionAttempt), workingDirectory);
        }
        catch
        {
            DeleteScratchDirectoryBestEffort(job.OperationId, ownedScratchDirectory);
            throw;
        }

        var (execution, process) = StartTrackedProcess(job, startInfo, ownedScratchDirectory);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Log.ProcessLaunched(_logger, job.OperationId, executable, process.Id);

        _ = MonitorAsync(execution, process);

        return LaunchedMarkerPrefix + job.OperationId;
    }

    private ProcessStartInfo PrepareStartInfo(LaunchContext launch, string workingDirectory)
    {
        var arguments = ResolveArguments(launch.Job.Spec.Parameters);
        var startInfo = new ProcessStartInfo
        {
            FileName = launch.Executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        SubprocessEnvironmentScrubber.Scrub(startInfo.Environment);
        foreach (var variable in BuildEnvironment(launch.Job, launch.ExecutionAttempt))
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        return startInfo;
    }

    private (ProcessExecution Execution, Process Process) StartTrackedProcess(
        ExecutionJobRecord job,
        ProcessStartInfo startInfo,
        string? ownedScratchDirectory)
    {
        var execution = new ProcessExecution(job.OperationId, ownedScratchDirectory);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            process.OutputDataReceived += (_, e) => execution.AppendTail(e.Data);
            process.ErrorDataReceived += (_, e) => execution.AppendTail(e.Data);
            EvictStaleTerminalExecution(job.OperationId);

            // Register before Start so a fast-exiting process cannot complete before it is observable.
            if (!_executions.TryAdd(job.OperationId, execution))
            {
                throw new InvalidOperationException($"A local process for job '{job.OperationId}' is already tracked.");
            }

            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start local process '{startInfo.FileName}'.");
            }

            return (execution, process);
        }
        catch
        {
            _executions.TryRemove(KeyValuePair.Create(job.OperationId, execution));
            execution.Dispose();
            process.Dispose();
            DeleteScratchDirectoryBestEffort(job.OperationId, ownedScratchDirectory);
            throw;
        }
    }

    private void EvictStaleTerminalExecution(string operationId)
    {
        // Evict a stale terminal entry so a retry launches cleanly; a still-running entry must fail.
        if (_executions.TryGetValue(operationId, out var existing) && existing.Snapshot().Status.IsTerminal())
        {
            existing.MarkObserved();
            EvictExecution(operationId, existing);
        }
    }

    private async Task MonitorAsync(ProcessExecution execution, Process process)
    {
        // MonitorAsync exclusively owns disposal of `process` from this point on (Launch
        // handed it off after a successful Start()); a using statement here documents
        // that ownership.
        using (process)
        {
            try
            {
                await process.WaitForExitAsync(execution.CancellationToken).ConfigureAwait(false);
                // Ensure the async output readers flush before the tail is read.
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                execution.CompleteFromExit(process.ExitCode);
                Log.ProcessExited(_logger, execution.OperationId, process.ExitCode);
            }
            catch (OperationCanceledException)
            {
                SubprocessEnvironmentScrubber.TryKill(process);
                execution.CompleteCancelled();
                Log.ProcessCancelled(_logger, execution.OperationId);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Deliberately broad: this is the terminal handler for a tracked monitor task:
                // any unexpected failure must still mark the execution Failed and release the pool slot.
                execution.CompleteFailed();
                Log.ProcessMonitorFailed(_logger, execution.OperationId, ex.Message);
            }
            finally
            {
                ReleaseSlot();
                ScheduleDeferredEviction(execution);
            }
        }
    }

    private void ScheduleDeferredEviction(ProcessExecution execution)
    {
        if (_disposed)
        {
            EvictExecution(execution.OperationId, execution);
            return;
        }

        _ = EvictAfterRetentionAsync(execution);
    }

    private async Task EvictAfterRetentionAsync(ProcessExecution execution)
    {
        try
        {
            if (TerminalRetentionTtl > TimeSpan.Zero)
            {
                await Task.Delay(TerminalRetentionTtl, _disposeCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Backend is disposing; Dispose() handles final cleanup.
            return;
        }
        catch (ObjectDisposedException)
        {
            // Backend already disposed.
            return;
        }

        ReclaimExpiredExecution(execution.OperationId, execution);
    }

    private void EvictExpiredTerminalExecutions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _executions)
        {
            if (entry.Value.IsExpired(now, TerminalRetentionTtl))
            {
                ReclaimExpiredExecution(entry.Key, entry.Value);
            }
        }
    }

    private void ReclaimExpiredExecution(string operationId, ProcessExecution execution)
    {
        if (!_executions.TryRemove(KeyValuePair.Create(operationId, execution)))
        {
            return;
        }

        if (!_disposed && execution.TakeUnobservedTerminalOutcome() is { } outcome)
        {
            RetainUnobservedOutcome(operationId, outcome);
        }

        var scratch = execution.ScratchDirectory;
        execution.Dispose();
        DeleteScratchDirectoryBestEffort(operationId, scratch);
    }

    private void RetainUnobservedOutcome(string operationId, TerminalOutcome outcome)
    {
        if (_disposed || !outcome.Status.IsTerminal())
        {
            return;
        }

        if (_unobservedOutcomes.Count >= MaxRetainedUnobservedOutcomes
            && !_unobservedOutcomes.ContainsKey(operationId))
        {
            EvictOldestUnobservedOutcome();
        }

        _unobservedOutcomes[operationId] = outcome;
        if (_disposed)
        {
            _unobservedOutcomes.TryRemove(operationId, out _);
        }
    }

    private void EvictOldestUnobservedOutcome()
    {
        string? oldestKey = null;
        var oldestRecordedAt = DateTimeOffset.MaxValue;
        foreach (var entry in _unobservedOutcomes)
        {
            if (entry.Value.RecordedAt < oldestRecordedAt)
            {
                oldestRecordedAt = entry.Value.RecordedAt;
                oldestKey = entry.Key;
            }
        }

        if (oldestKey is not null)
        {
            _unobservedOutcomes.TryRemove(oldestKey, out _);
        }
    }

    private void ReleaseSlot()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _slots.Release();
        }
        catch (ObjectDisposedException)
        {
            // The backend was disposed while this monitor was still finishing; nothing to release.
        }
    }

    private void EvictExecution(string operationId, ProcessExecution? expected = null)
    {
        ProcessExecution? execution;
        if (expected is not null)
        {
            if (!_executions.TryRemove(KeyValuePair.Create(operationId, expected)))
            {
                return;
            }

            execution = expected;
        }
        else if (!_executions.TryRemove(operationId, out execution))
        {
            return;
        }

        var scratch = execution.ScratchDirectory;
        execution.Dispose();
        DeleteScratchDirectoryBestEffort(operationId, scratch);
    }

    private void DeleteScratchDirectoryBestEffort(string operationId, string? scratch)
    {
        if (string.IsNullOrEmpty(scratch))
        {
            return;
        }

        try
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup: a leftover scratch dir is harmless and is reclaimed on the next attempt.
            Log.ScratchCleanupFailed(_logger, operationId, ex.Message);
        }
    }

    private static bool IsPendingMarker(string? providerOperationId)
        => !string.IsNullOrEmpty(providerOperationId)
            && providerOperationId.StartsWith(PendingSubmissionMarkerPrefix, StringComparison.Ordinal);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Signal disposal before cancelling monitors so a monitor draining concurrently can skip its
        // slot release fast-path; the release itself is still guarded against the disposal race.
        _disposed = true;
        try
        {
            _disposeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        foreach (var entry in _executions)
        {
            entry.Value.RequestCancel();
        }

        foreach (var entry in _executions.ToArray())
        {
            EvictExecution(entry.Key, entry.Value);
        }

        _unobservedOutcomes.Clear();
        _disposeCts.Dispose();
        _slots.Dispose();
    }

    internal static partial class Log
    {
        [LoggerMessage(9300, LogLevel.Information, "Launched local process for execution job {OperationId}: {Executable} (pid {ProcessId})")]
        public static partial void ProcessLaunched(ILogger logger, string operationId, string executable, int processId);

        [LoggerMessage(9301, LogLevel.Information, "Local process for execution job {OperationId} exited with code {ExitCode}")]
        public static partial void ProcessExited(ILogger logger, string operationId, int exitCode);

        [LoggerMessage(9302, LogLevel.Information, "Local process for execution job {OperationId} was cancelled")]
        public static partial void ProcessCancelled(ILogger logger, string operationId);

        [LoggerMessage(9303, LogLevel.Warning, "Local process launch rejected for execution job {OperationId}: {Reason}")]
        public static partial void LaunchRejected(ILogger logger, string operationId, string reason);

        [LoggerMessage(9304, LogLevel.Warning, "Local process launch failed for execution job {OperationId}: {Reason}")]
        public static partial void LaunchFailed(ILogger logger, string operationId, string reason);

        [LoggerMessage(9305, LogLevel.Debug, "Local process pool saturated; deferring execution job {OperationId} (max {MaxConcurrent})")]
        public static partial void PoolSaturated(ILogger logger, string operationId, int maxConcurrent);

        [LoggerMessage(9306, LogLevel.Warning, "Local process for execution job {OperationId} is no longer tracked (provider id {ProviderOperationId})")]
        public static partial void ProcessLost(ILogger logger, string operationId, string providerOperationId);

        [LoggerMessage(9307, LogLevel.Warning, "Local process monitor failed for execution job {OperationId}: {Reason}")]
        public static partial void ProcessMonitorFailed(ILogger logger, string operationId, string reason);

        [LoggerMessage(9308, LogLevel.Debug, "Failed to delete scratch directory for execution job {OperationId}: {Reason}")]
        public static partial void ScratchCleanupFailed(ILogger logger, string operationId, string reason);
    }
}
