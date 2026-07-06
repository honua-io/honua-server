// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

/// <summary>
/// Parameter keys read from <see cref="ExecutionJobSpec.Parameters"/> by the local process-pool
/// batch backend. The executable and its ordered arguments describe the child process to launch;
/// any <c>env.&lt;NAME&gt;</c> entry is injected as an environment variable alongside the standard
/// <c>HONUA_*</c> variables the cloud backends inject.
/// </summary>
internal static class LocalProcessParameterKeys
{
    /// <summary>Absolute path (or PATH-resolvable name) of the executable to launch.</summary>
    public const string Executable = "process.executable";

    /// <summary>
    /// Prefix for ordered positional arguments: <c>process.arg.0</c>, <c>process.arg.1</c>, ...
    /// Arguments are collected in ascending numeric order and passed as an argument list (never a
    /// shell command string) so there is no shell interpretation or injection surface.
    /// </summary>
    public const string ArgumentPrefix = "process.arg.";

    /// <summary>Optional per-job working directory override; defaults to a scratch dir under the pool root.</summary>
    public const string WorkingDirectory = "process.working_dir";

    /// <summary>Prefix for environment variables passed through to the child process.</summary>
    public const string EnvironmentPrefix = "env.";
}

/// <summary>
/// Substrate-neutral, single-host batch-compute backend (ADR-0060) that launches run-to-completion
/// workloads as child processes bounded by a configurable max-concurrency pool. This is the true
/// process-spawning geoprocessing executor for single-host / air-gapped deployments: it needs no
/// container runtime and no cloud batch service.
/// </summary>
/// <remarks>
/// <para>
/// Peer to (not a replacement for) <see cref="LocalBatchComputeBackend"/>: the in-process
/// <c>local</c> backend bridges progress from the in-process worker substrate, whereas this backend
/// actually spawns an external OS process per job.
/// </para>
/// <para>
/// State discipline: launched processes are tracked in an in-process registry keyed by operation id.
/// This state cannot survive a host restart, so <see cref="ObserveAsync"/> reports a job whose
/// launched process is no longer tracked as <see cref="ExecutionJobStatus.Failed"/> with a clear
/// "process lost" message; the reconciler's retry machinery then re-queues it. Admission is
/// non-blocking: when the pool is saturated the backend returns <see cref="ExecutionJobStatus.Provisioning"/>
/// with a pending marker so the reconciler keeps the record active and a later reconcile launches the
/// process once a slot frees, rather than holding the reconciliation lease on a blocking wait.
/// </para>
/// </remarks>
internal sealed partial class LocalProcessPoolBatchComputeBackend : IBatchComputeBackend, IDisposable
{
    internal const string AdapterBackendName = "honua-local-process";

    /// <summary>Provider-id marker for a job admitted to the backend but not yet launched (pool saturated).</summary>
    internal const string PendingSubmissionMarkerPrefix = "local-process-pending:";

    /// <summary>Provider-id prefix stamped once the child process has actually been launched.</summary>
    internal const string LaunchedMarkerPrefix = "local-process:";

    private const int MaxTailLines = 50;

    private static readonly BatchComputeBackendCapabilities CapabilitiesSnapshot = new()
    {
        SupportsCancellation = true,
        SupportsProgressPolling = true,
        // The reconciler re-queues a Failed job when the backend supports retry; that is exactly how a
        // process lost to a host restart recovers (a fresh launch on the next attempt).
        SupportsRetry = true,
        SupportsLogStreaming = false,
        SupportsArtifactStaging = false
    };

    private readonly ILogger<LocalProcessPoolBatchComputeBackend> _logger;
    private readonly LocalProcessPoolOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<string, ProcessExecution> _executions = new(StringComparer.Ordinal);
    private volatile bool _disposed;

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
        => Task.FromResult(CapabilitiesSnapshot);

    public Task<BatchComputeSubmissionResult> StartAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        string executable;
        try
        {
            executable = ResolveExecutable(job.Spec.Parameters);
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
            var providerId = Launch(job, executable);
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

        if (_executions.TryGetValue(job.OperationId, out var execution))
        {
            var snapshot = execution.Snapshot();

            // Report the terminal status once, then evict the tracking entry and its scratch directory.
            // Eviction here (rather than in MonitorAsync's finally) preserves ObserveAsync's contract that
            // a just-finished job still reports its true terminal status, while making the retry path work:
            // once evicted, a reconciler requeue of the same OperationId re-enters StartAsync/Launch and
            // launches cleanly instead of colliding on "already tracked". It also bounds _executions and
            // reclaims orphaned working directories. A subsequent observe after eviction falls through to
            // the not-tracked branch below, which echoes the persisted terminal status on the job record.
            if (IsTerminal(snapshot.Status))
            {
                EvictExecution(job.OperationId);
            }

            return Task.FromResult(new BatchComputeObservation
            {
                Status = snapshot.Status,
                ProviderOperationId = job.ProviderOperationId,
                PercentComplete = snapshot.Status == ExecutionJobStatus.Succeeded ? 100 : job.PercentComplete,
                Message = snapshot.Message
            });
        }

        // Not tracked. Either admitted-but-not-yet-launched (pool was saturated at submit) or a launched
        // process whose in-process state was lost to a host restart.
        if (IsPendingMarker(job.ProviderOperationId))
        {
            return Task.FromResult(TryLaunchPending(job));
        }

        // Already-terminal record whose execution was observed and evicted on a prior poll: echo the
        // persisted terminal status rather than declaring the process lost (which would flip a Succeeded
        // job to Failed).
        if (IsTerminal(job.Status))
        {
            return Task.FromResult(new BatchComputeObservation
            {
                Status = job.Status,
                ProviderOperationId = job.ProviderOperationId,
                PercentComplete = job.Status == ExecutionJobStatus.Succeeded ? 100 : job.PercentComplete,
                Message = "Local process already reached a terminal state."
            });
        }

        Log.ProcessLost(_logger, job.OperationId, job.ProviderOperationId ?? "<none>");
        return Task.FromResult(new BatchComputeObservation
        {
            Status = ExecutionJobStatus.Failed,
            ProviderOperationId = job.ProviderOperationId,
            Message = "Local process is no longer tracked (host restart?); the job will be retried."
        });
    }

    public Task<BatchComputeObservation> CancelAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

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
        }
        catch (InvalidOperationException ex)
        {
            return new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Failed,
                ProviderOperationId = job.ProviderOperationId,
                Message = ex.Message
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
            var providerId = Launch(job, executable);
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

    private string Launch(ExecutionJobRecord job, string executable)
    {
        var arguments = ResolveArguments(job.Spec.Parameters);
        var (workingDirectory, ownedScratch) = ResolveWorkingDirectory(job);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
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

        foreach (var variable in BuildEnvironment(job))
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        var execution = new ProcessExecution(job.OperationId, ownedScratch ? workingDirectory : null);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => execution.AppendTail(e.Data);
        process.ErrorDataReceived += (_, e) => execution.AppendTail(e.Data);

        // A previous attempt for this OperationId that already finished may still be tracked (for
        // example when a reconciler requeue lands before the terminal status was observed and evicted).
        // Evict a stale terminal entry so the retry launches cleanly; a still-running entry is a genuine
        // double-launch and must fail.
        if (_executions.TryGetValue(job.OperationId, out var existing) && IsTerminal(existing.Snapshot().Status))
        {
            EvictExecution(job.OperationId);
        }

        // Register before Start so a fast-exiting process cannot complete before it is observable.
        if (!_executions.TryAdd(job.OperationId, execution))
        {
            throw new InvalidOperationException($"A local process for job '{job.OperationId}' is already tracked.");
        }

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start local process '{executable}'.");
            }
        }
        catch
        {
            _executions.TryRemove(job.OperationId, out _);
            process.Dispose();
            throw;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Log.ProcessLaunched(_logger, job.OperationId, executable, process.Id);

        // Fire-and-forget monitor: awaits exit, records terminal status, and releases the pool slot.
        _ = MonitorAsync(execution, process);

        return LaunchedMarkerPrefix + job.OperationId;
    }

    private async Task MonitorAsync(ProcessExecution execution, Process process)
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
            TryKill(process);
            execution.CompleteCancelled();
            Log.ProcessCancelled(_logger, execution.OperationId);
        }
        catch (Exception ex)
        {
            execution.CompleteFailed();
            Log.ProcessMonitorFailed(_logger, execution.OperationId, ex.Message);
        }
        finally
        {
            process.Dispose();
            ReleaseSlot();
        }
    }

    /// <summary>
    /// Releases a pool slot, tolerating a concurrent <see cref="Dispose"/> that already disposed the
    /// semaphore. A monitor task can still be draining when the backend is torn down, so the release
    /// races the disposal; swallowing the disposed-object signal keeps shutdown from surfacing a
    /// spurious error.
    /// </summary>
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

    private static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled;

    /// <summary>
    /// Removes a tracked execution and best-effort deletes the scratch directory the backend created
    /// for it. Caller-supplied working directories are never deleted (only backend-owned scratch dirs
    /// under <see cref="LocalProcessPoolOptions.WorkingRoot"/> are).
    /// </summary>
    private void EvictExecution(string operationId)
    {
        if (!_executions.TryRemove(operationId, out var execution))
        {
            return;
        }

        var scratch = execution.ScratchDirectory;
        execution.Dispose();
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

    private (string Directory, bool OwnedScratch) ResolveWorkingDirectory(ExecutionJobRecord job)
    {
        if (job.Spec.Parameters.TryGetValue(LocalProcessParameterKeys.WorkingDirectory, out var configured)
            && !string.IsNullOrWhiteSpace(configured))
        {
            Directory.CreateDirectory(configured);
            return (configured, false);
        }

        var root = string.IsNullOrWhiteSpace(_options.WorkingRoot)
            ? Path.Combine(Path.GetTempPath(), "honua-local-process")
            : _options.WorkingRoot;
        var jobDirectory = Path.Combine(root, SanitizeDirectorySegment(job.OperationId));
        Directory.CreateDirectory(jobDirectory);
        return (jobDirectory, true);
    }

    private static string ResolveExecutable(IReadOnlyDictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue(LocalProcessParameterKeys.Executable, out var executable)
            || string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException(
                $"Local process execution requires parameter '{LocalProcessParameterKeys.Executable}'.");
        }

        return executable.Trim();
    }

    private static string[] ResolveArguments(IReadOnlyDictionary<string, string> parameters)
    {
        var ordered = new SortedDictionary<int, string>();
        foreach (var entry in parameters)
        {
            if (!entry.Key.StartsWith(LocalProcessParameterKeys.ArgumentPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var indexText = entry.Key[LocalProcessParameterKeys.ArgumentPrefix.Length..];
            if (int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                ordered[index] = entry.Value ?? string.Empty;
            }
        }

        return ordered.Count == 0 ? Array.Empty<string>() : ordered.Values.ToArray();
    }

    private static List<KeyValuePair<string, string>> BuildEnvironment(ExecutionJobRecord job)
    {
        // Mirror AwsBatchComputeBackend.BuildEnvironmentOverrides variable names so a workload behaves
        // identically whether it runs as a child process here or as a container on a cloud batch service.
        var variables = new List<KeyValuePair<string, string>>
        {
            new("HONUA_OPERATION_ID", job.OperationId),
            new("HONUA_WORKLOAD_NAME", job.Spec.WorkloadName),
            new("HONUA_JOB_KIND", job.Spec.Kind.ToString())
        };

        if (!string.IsNullOrWhiteSpace(job.Spec.WorkloadId))
        {
            variables.Add(new("HONUA_WORKLOAD_ID", job.Spec.WorkloadId));
        }

        if (!string.IsNullOrWhiteSpace(job.Spec.RuntimeProfile))
        {
            variables.Add(new("HONUA_RUNTIME_PROFILE", job.Spec.RuntimeProfile));
        }

        foreach (var entry in job.Spec.Parameters)
        {
            if (!entry.Key.StartsWith(LocalProcessParameterKeys.EnvironmentPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = entry.Key[LocalProcessParameterKeys.EnvironmentPrefix.Length..];
            if (string.IsNullOrWhiteSpace(name)
                || string.Equals(name, "HONUA_CONTRACT_VERSION", StringComparison.Ordinal))
            {
                // Never let a workload passthrough shadow the contract-version gate; it is stamped last.
                continue;
            }

            variables.Add(new(name, entry.Value ?? string.Empty));
        }

        // Serving↔worker job-contract version (ADR-0060 #3b): stamped AFTER the env.* passthrough so a
        // workload-supplied env.HONUA_CONTRACT_VERSION can never override the gate value. Mirrors the
        // cloud backends so a workload behaves identically as a child process or a batch container.
        variables.Add(new("HONUA_CONTRACT_VERSION", job.Spec.ContractVersion.ToString(CultureInfo.InvariantCulture)));

        return variables;
    }

    private static string SanitizeDirectorySegment(string operationId)
    {
        var chars = operationId.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var ch = chars[i];
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_'))
            {
                chars[i] = '-';
            }
        }

        var sanitized = new string(chars);
        return string.IsNullOrWhiteSpace(sanitized) ? "job" : sanitized;
    }

    private static bool IsPendingMarker(string? providerOperationId)
        => !string.IsNullOrEmpty(providerOperationId)
            && providerOperationId.StartsWith(PendingSubmissionMarkerPrefix, StringComparison.Ordinal);

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the check and the kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Permission/race killing the process; nothing more we can do.
        }
    }

    public void Dispose()
    {
        // Signal disposal before cancelling monitors so a monitor draining concurrently can skip its
        // slot release fast-path; the release itself is still guarded against the disposal race.
        _disposed = true;
        foreach (var execution in _executions.Values)
        {
            execution.RequestCancel();
        }

        _slots.Dispose();
    }

    /// <summary>
    /// In-process tracking state for one launched child process. Terminal status is recorded by the
    /// monitor task once the process exits so <see cref="ObserveAsync"/> can report it.
    /// </summary>
    private sealed class ProcessExecution(string operationId, string? scratchDirectory) : IDisposable
    {
        private readonly object _sync = new();
        private readonly Queue<string> _tail = new();
        private readonly CancellationTokenSource _cts = new();
        private ExecutionJobStatus _status = ExecutionJobStatus.Running;
        private string? _message;

        public string OperationId { get; } = operationId;

        /// <summary>Backend-owned scratch directory to reclaim on eviction, or null for a caller-supplied dir.</summary>
        public string? ScratchDirectory { get; } = scratchDirectory;

        public CancellationToken CancellationToken => _cts.Token;

        public void RequestCancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already completed and disposed.
            }
        }

        public void AppendTail(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_sync)
            {
                _tail.Enqueue(line);
                while (_tail.Count > MaxTailLines)
                {
                    _tail.Dequeue();
                }
            }
        }

        public void CompleteFromExit(int exitCode)
        {
            lock (_sync)
            {
                _status = exitCode == 0 ? ExecutionJobStatus.Succeeded : ExecutionJobStatus.Failed;
                _message = exitCode == 0
                    ? "Local process completed successfully."
                    : $"Local process exited with code {exitCode.ToString(CultureInfo.InvariantCulture)}.{FormatTail()}";
            }

            DisposeToken();
        }

        public void CompleteCancelled()
        {
            lock (_sync)
            {
                _status = ExecutionJobStatus.Cancelled;
                _message = "Local process was cancelled.";
            }

            DisposeToken();
        }

        public void CompleteFailed()
        {
            lock (_sync)
            {
                _status = ExecutionJobStatus.Failed;
                _message = "Local process monitoring failed.";
            }

            DisposeToken();
        }

        public (ExecutionJobStatus Status, string? Message) Snapshot()
        {
            lock (_sync)
            {
                return (_status, _message);
            }
        }

        private string FormatTail()
        {
            if (_tail.Count == 0)
            {
                return string.Empty;
            }

            var tail = string.Join(" ", _tail);
            if (tail.Length > 500)
            {
                tail = tail[^500..];
            }

            return $" Output tail: {tail}";
        }

        private void DisposeToken()
        {
            try
            {
                _cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose() => DisposeToken();
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

/// <summary>
/// Options for the single-host local process-pool batch backend (<c>ControlPlane:LocalProcess</c>).
/// </summary>
internal sealed class LocalProcessPoolOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ControlPlane:LocalProcess";

    /// <summary>
    /// Maximum number of concurrently running child processes. Bounds host resource pressure on a
    /// single-node deployment. Defaults to 2.
    /// </summary>
    public int MaxConcurrentProcesses { get; set; } = 2;

    /// <summary>
    /// Root scratch directory under which each job gets a working directory. When unset, a
    /// <c>honua-local-process</c> folder under the system temp directory is used.
    /// </summary>
    public string? WorkingRoot { get; set; }
}

/// <summary>
/// Validates <see cref="LocalProcessPoolOptions"/> at startup.
/// </summary>
internal sealed class LocalProcessPoolOptionsValidator : OptionsValidator<LocalProcessPoolOptions>
{
    protected override void ValidateOptions(LocalProcessPoolOptions options, List<string> failures)
    {
        if (options.MaxConcurrentProcesses <= 0)
        {
            failures.Add($"{LocalProcessPoolOptions.SectionName}:MaxConcurrentProcesses must be greater than 0.");
        }
    }
}
