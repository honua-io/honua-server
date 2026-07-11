// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing.CustomCode.LocalBackend;

/// <summary>
/// Opt-in <see cref="IBatchComputeBackend"/> that runs an admitted, allowlisted, git-pinned
/// custom-code job as an OS-sandboxed subprocess on the honua-server host itself — the no-cloud-infra
/// alternative to the AWS-Batch custom-code path for single-host / air-gapped deployments.
/// </summary>
/// <remarks>
/// <para>
/// <b>SECURITY MODEL — READ BEFORE ENABLING. This surface executes untrusted user code on the server
/// host.</b> Corrected after adversarial review: an earlier revision overstated the environment
/// allowlist's strength and omitted UID separation and a raw-token leak entirely — see below and
/// <see cref="CustomCodeLocalBackendOptions"/> for the full, honest picture.
/// </para>
/// <list type="number">
///   <item><description><b>Process UID separation (<see cref="CustomCodeLocalBackendOptions.SandboxUser"/>,
///   POSIX).</b> The control the others depend on for real strength. WITHOUT it, the subprocess runs
///   under the SAME OS user as honua-server: a same-UID script can unconditionally read any file
///   honua-server itself can read via plain DAC file permissions, and — on hosts where Linux's Yama
///   LSM allows it (<c>kernel.yama.ptrace_scope=0</c>; NOT the Ubuntu-style restricted default) — read
///   <c>/proc/&lt;honua-server-pid&gt;/environ</c> directly to recover honua-server's ENTIRE process
///   environment regardless of the allowlist below, and signal honua-server. WITH it, the child is
///   switched to a distinct, unprivileged user via <c>setpriv</c> before its target program runs,
///   closing all of the above regardless of the host's Yama configuration. Requires honua-server to
///   have <c>CAP_SETUID</c>; if the drop fails, the launch fails closed. Without
///   <see cref="CustomCodeLocalBackendOptions.SandboxUser"/> the operator must set
///   <see cref="CustomCodeLocalBackendOptions.AcknowledgeUnconfinedExecutionRisk"/> — a code-enforced
///   gate the backend checks at every <see cref="StartAsync"/>, not just at startup — or the backend
///   refuses to run any job.</description></item>
///   <item><description><b>Environment allowlist.</b> The subprocess inherits NOTHING via the
///   environment vector: only the custom-code contract variables (<c>CUSTOMCODE_*</c> and the
///   job-scoped <c>HONUA_BASE_URL</c>), a controlled minimal <c>PATH</c>, the standard <c>HONUA_*</c>
///   job markers, and any names the operator explicitly listed in
///   <see cref="CustomCodeLocalBackendOptions.EnvironmentAllowlist"/> are exposed. The raw
///   <c>HONUA_JOB_TOKEN</c> callback credential is deliberately NEVER copied into the child's
///   environment (see <see cref="BuildEnvironment"/>) — this MVP does not yet give a local custom tool
///   a Honua API callback client. This is a real confidentiality boundary only in combination with UID
///   separation above; without it, same-UID file/proc access defeats it (see item 1).</description></item>
///   <item><description><b>Wall-clock timeout + process-tree kill.</b> A monitor kills the entire
///   process tree at <see cref="CustomCodeLocalBackendOptions.MaxWallClock"/>. A detached
///   double-forked grandchild can survive the tree-kill, and because the CPU limit below counts
///   CPU-seconds (not wall-clock), a mostly-sleeping escapee can persist a long time — even across a
///   honua-server restart — on a bare host; a container/PID-namespace boundary closes this by tearing
///   down the whole namespace on exit.</description></item>
///   <item><description><b>CPU + address-space + output-size limits (POSIX).</b> The child is launched
///   through a kernel-enforced <c>ulimit</c> wrapper (RLIMIT_CPU / RLIMIT_AS / RLIMIT_FSIZE); a limit
///   the wrapper fails to apply now aborts the launch (fails closed) rather than silently proceeding
///   unconfined. <see cref="CustomCodeLocalBackendOptions.MaxProcessCount"/> (RLIMIT_NPROC) applies
///   only alongside <see cref="CustomCodeLocalBackendOptions.SandboxUser"/> (RLIMIT_NPROC is per-UID
///   host-wide on Linux). Not enforced in-process on non-POSIX hosts — run inside a
///   cgroup-constrained container there.</description></item>
///   <item><description><b>Single-use scratch confinement + path-traversal safety.</b> A fresh
///   directory is created per job under <see cref="CustomCodeLocalBackendOptions.WorkingRoot"/>, is the
///   only path handed to the child, and is deleted on terminal. Every constructed path is validated to
///   resolve strictly under that root (<see cref="SandboxPaths"/>) — lexically, not symlink-resolving;
///   this is subsumed by UID separation (item 1) and fully closing it needs a mount-namespace/container
///   boundary.</description></item>
///   <item><description><b>Network (NOT enforced here).</b> OS-process-level network denial is not
///   portably enforceable without a namespace/container boundary this MVP does not own. Treat network
///   isolation as a deployment requirement: run this backend inside an already-network-restricted
///   container/namespace. The one intentional network operation — the git checkout of the pinned commit
///   — happens in the honua-server process, not the sandbox.</description></item>
/// </list>
/// <para>
/// The repo-allowlist / signed-commit gate (<see cref="CustomCodeRepoPolicy"/>) and the SHA-pin,
/// scope-clamp, and scoped-token mint are all enforced upstream at submit time and are unchanged; this
/// backend re-validates its inputs for defense in depth but is not the trust boundary for which repos
/// may run. <b>This code needs human security review, not just automated review, before it executes
/// untrusted code in any real deployment.</b>
/// </para>
/// </remarks>
internal sealed partial class LocalProcessCustomCodeBackend : IBatchComputeBackend, IDisposable
{
    internal const string AdapterBackendName = "honua-local-customcode";

    /// <summary>Provider-id prefix stamped once the sandboxed process has been launched.</summary>
    internal const string LaunchedMarkerPrefix = "local-customcode:";

    private const int MaxTailLines = 50;

    private static readonly BatchComputeBackendCapabilities CapabilitiesSnapshot = new()
    {
        SupportsCancellation = true,
        SupportsProgressPolling = true,
        // A process lost to a host restart is reported Failed; the reconciler's retry re-runs it.
        SupportsRetry = true,
        SupportsLogStreaming = false,
        SupportsArtifactStaging = false,
    };

    private readonly IOptionsMonitor<CustomCodeLocalBackendOptions> _options;
    private readonly ICustomCodeWorkloadPreparer _preparer;
    private readonly ILogger<LocalProcessCustomCodeBackend> _logger;
    private readonly ConcurrentDictionary<string, CustomCodeExecution> _executions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots;
    private volatile bool _disposed;

    public LocalProcessCustomCodeBackend(
        IOptionsMonitor<CustomCodeLocalBackendOptions> options,
        ICustomCodeWorkloadPreparer preparer,
        ILogger<LocalProcessCustomCodeBackend> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _preparer = preparer ?? throw new ArgumentNullException(nameof(preparer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var maxConcurrent = Math.Max(1, options.CurrentValue.MaxConcurrentProcesses);
        _slots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public string BackendName => AdapterBackendName;

    public BatchComputeTargetKind TargetKind => BatchComputeTargetKind.LocalProcess;

    public Task<BatchComputeBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CapabilitiesSnapshot);

    public async Task<BatchComputeSubmissionResult> StartAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        var options = _options.CurrentValue;

        // Fail closed unless the operator explicitly enabled this untrusted-code surface, even if the
        // backend was selected. This is the belt-and-suspenders gate on top of the Backend=Local choice.
        if (!options.Enabled)
        {
            Log.BackendDisabled(_logger, job.OperationId);
            return Failed("The local custom-code backend is not enabled (set Geoprocessing:CustomCode:Local:Enabled=true).");
        }

        // F1 (adversarial review): re-check the UID-separation gate on every StartAsync, not just at
        // options-validator startup time — IOptionsMonitor can observe a config reload, and this is the
        // load-bearing control the environment allowlist depends on for real strength (see class docs).
        if (string.IsNullOrWhiteSpace(options.SandboxUser) && !options.AcknowledgeUnconfinedExecutionRisk)
        {
            Log.UnconfinedExecutionRefused(_logger, job.OperationId);
            return Failed(
                "The local custom-code backend requires either Geoprocessing:CustomCode:Local:SandboxUser " +
                "(recommended — drops the child to a distinct unprivileged OS user) or an explicit " +
                "Geoprocessing:CustomCode:Local:AcknowledgeUnconfinedExecutionRisk=true. Without a sandbox " +
                "user the subprocess runs under the SAME OS user as honua-server and can read any file " +
                "honua-server can read (and, depending on the host, honua-server's process environment via " +
                "/proc); refusing to run closed.");
        }

        CustomCodeJobInputs inputs;
        try
        {
            inputs = ReadInputs(job.Spec.Parameters);
        }
        catch (InvalidOperationException ex)
        {
            Log.LaunchRejected(_logger, job.OperationId, ex.Message);
            return Failed(ex.Message);
        }

        // Non-blocking admission: never hold the reconciliation lease waiting for a slot.
        if (!_slots.Wait(0, CancellationToken.None))
        {
            Log.PoolSaturated(_logger, job.OperationId, options.MaxConcurrentProcesses);
            return new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Provisioning,
                Message = $"Waiting for a local custom-code slot (max {options.MaxConcurrentProcesses} concurrent).",
            };
        }

        try
        {
            return await LaunchAsync(job, inputs, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _slots.Release();
            Log.LaunchFailed(_logger, job.OperationId, ex.Message);
            return Failed(SafeFailureMessage(ex));
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
            if (IsTerminal(snapshot.Status))
            {
                EvictExecution(job.OperationId);
            }

            return Task.FromResult(new BatchComputeObservation
            {
                Status = snapshot.Status,
                ProviderOperationId = job.ProviderOperationId,
                PercentComplete = snapshot.Status == ExecutionJobStatus.Succeeded ? 100 : job.PercentComplete,
                Message = snapshot.Message,
            });
        }

        // A record whose execution already reached terminal and was evicted: echo the persisted status
        // rather than declaring the process lost (which would flip a Succeeded job to Failed).
        if (IsTerminal(job.Status))
        {
            return Task.FromResult(new BatchComputeObservation
            {
                Status = job.Status,
                ProviderOperationId = job.ProviderOperationId,
                PercentComplete = job.Status == ExecutionJobStatus.Succeeded ? 100 : job.PercentComplete,
                Message = "Local custom-code job already reached a terminal state.",
            });
        }

        // A launched marker with no tracked execution models a process lost to a host restart.
        Log.ProcessLost(_logger, job.OperationId, job.ProviderOperationId ?? "<none>");
        return Task.FromResult(new BatchComputeObservation
        {
            Status = ExecutionJobStatus.Failed,
            ProviderOperationId = job.ProviderOperationId,
            Message = "Local custom-code process is no longer tracked (host restart?); the job will be retried.",
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
                Message = "Local custom-code cancellation requested.",
            });
        }

        return Task.FromResult(new BatchComputeObservation
        {
            Status = ExecutionJobStatus.Cancelled,
            ProviderOperationId = job.ProviderOperationId,
            Message = "Local custom-code process was not tracked; treating cancellation as completed.",
        });
    }

    private async Task<BatchComputeSubmissionResult> LaunchAsync(
        ExecutionJobRecord job,
        CustomCodeJobInputs inputs,
        CustomCodeLocalBackendOptions options,
        CancellationToken cancellationToken)
    {
        // A prior terminal execution for the same OperationId (a reconciler requeue that landed before
        // the terminal status was observed and evicted) is evicted so the retry launches cleanly; a
        // still-running one is a genuine double-launch and must fail.
        if (_executions.TryGetValue(job.OperationId, out var existing))
        {
            if (IsTerminal(existing.Snapshot().Status))
            {
                EvictExecution(job.OperationId);
            }
            else
            {
                _slots.Release();
                return Failed($"A local custom-code process for job '{job.OperationId}' is already running.");
            }
        }

        // Single-use scratch directory, fresh per job, under the configured root. The checkout lives in
        // a subdirectory of it; both are validated to be strictly contained.
        var scratchDirectory = CreateScratchDirectory(job.OperationId, options);
        var checkoutDirectory = SandboxPaths.ResolveContained(scratchDirectory, "checkout");

        SandboxLaunchSpec launchSpec;
        try
        {
            launchSpec = await _preparer.PrepareAsync(inputs, checkoutDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CustomCodeWorkloadPreparationException or CustomCodePathEscapeException)
        {
            _slots.Release();
            TryDeleteDirectory(scratchDirectory, job.OperationId);
            Log.LaunchRejected(_logger, job.OperationId, ex.Message);
            return Failed(ex.Message);
        }

        var environment = BuildEnvironment(job, inputs, options);
        var process = SandboxedProcess.Create(launchSpec, scratchDirectory, environment, options);
        var execution = new CustomCodeExecution(job.OperationId, scratchDirectory, options.MaxWallClock);
        process.OutputDataReceived += (_, e) => execution.AppendTail(e.Data);
        process.ErrorDataReceived += (_, e) => execution.AppendTail(e.Data);

        if (!_executions.TryAdd(job.OperationId, execution))
        {
            _slots.Release();
            process.Dispose();
            TryDeleteDirectory(scratchDirectory, job.OperationId);
            return Failed($"A local custom-code process for job '{job.OperationId}' is already tracked.");
        }

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start the sandboxed custom-code process.");
            }
        }
        catch
        {
            // Cleanup only; the slot is released by StartAsync's catch after this rethrow (releasing
            // here too would double-release and inflate the pool).
            _executions.TryRemove(job.OperationId, out _);
            process.Dispose();
            TryDeleteDirectory(scratchDirectory, job.OperationId);
            throw;
        }

        // Close stdin immediately: user code that blocks on stdin cannot hold the slot open.
        try { process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Log.ProcessLaunched(_logger, job.OperationId, process.Id, (long)options.MaxWallClock.TotalSeconds);

        _ = MonitorAsync(execution, process);

        return new BatchComputeSubmissionResult
        {
            Status = ExecutionJobStatus.Running,
            ProviderOperationId = LaunchedMarkerPrefix + job.OperationId,
            Message = $"Launched sandboxed custom-code process for job '{job.OperationId}'.",
        };
    }

    private async Task MonitorAsync(CustomCodeExecution execution, Process process)
    {
        try
        {
            // Wall-clock deadline: link the operator cancel token with a deadline timer. Whichever
            // trips, we hard-kill the whole process tree so a spawned child cannot survive.
            using var deadlineCts = new CancellationTokenSource(execution.WallClock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                deadlineCts.Token, execution.CancellationToken);

            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                // Ensure async output readers flush before the tail is read.
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                execution.CompleteFromExit(process.ExitCode);
                Log.ProcessExited(_logger, execution.OperationId, process.ExitCode);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (deadlineCts.IsCancellationRequested && !execution.CancellationToken.IsCancellationRequested)
                {
                    execution.CompleteTimedOut();
                    Log.ProcessTimedOut(_logger, execution.OperationId, (long)execution.WallClock.TotalSeconds);
                }
                else
                {
                    execution.CompleteCancelled();
                    Log.ProcessCancelled(_logger, execution.OperationId);
                }

                // Drain so the process object's exit is observed before disposal.
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) when (ex is InvalidOperationException) { }
            }
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
            // Disposed while this monitor was still finishing; nothing to release.
        }
    }

    /// <summary>
    /// Reads and re-validates the custom-code inputs off the durable spec. These were validated at
    /// submit time; re-checking here keeps the backend a defense-in-depth boundary rather than trusting
    /// the record blindly.
    /// </summary>
    private static CustomCodeJobInputs ReadInputs(IReadOnlyDictionary<string, string> parameters)
    {
        var runtime = Require(parameters, CustomCodeJobContract.RuntimeParam);
        var repoUrl = Require(parameters, CustomCodeJobContract.RepoUrlParam);
        var gitRef = Require(parameters, CustomCodeJobContract.GitRefParam);
        var entrypoint = Require(parameters, CustomCodeJobContract.EntrypointParam);
        var depsManifest = Require(parameters, CustomCodeJobContract.DepsManifestParam);

        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Custom-code repo_url must be an absolute https URL.");
        }

        if (gitRef.Length != 40 || !gitRef.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("Custom-code git_ref must be a full 40-hex commit SHA.");
        }

        if (Path.IsPathRooted(depsManifest) || depsManifest.Split('/', '\\').Contains(".."))
        {
            throw new InvalidOperationException("Custom-code deps_manifest must be a repo-relative path without traversal.");
        }

        return new CustomCodeJobInputs(runtime, repoUrl, gitRef, entrypoint, depsManifest);
    }

    private static string Require(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new InvalidOperationException($"Local custom-code execution requires spec parameter '{key}'.");

    /// <summary>
    /// Builds the EXACT environment the subprocess sees as a strict allowlist. Nothing is copied
    /// wholesale from the parent; each entry is added by name.
    /// </summary>
    /// <remarks>
    /// F2 (adversarial review): the raw <c>HONUA_JOB_TOKEN</c> callback credential is deliberately NEVER
    /// added here, unlike the cloud custom-code path's harness (<c>sandbox.py</c>), which constructs a
    /// scoped client from it and then scrubs it from the environment before user code runs. This MVP
    /// local backend has no equivalent scoped-client construction (no guaranteed <c>honua_sdk</c> on the
    /// host, no network into the sandbox to use it even if present), so rather than hand a local custom
    /// tool a raw bearer token it cannot safely use, this backend does not expose one at all. A tool that
    /// needs to call back into honua-server's API is not yet supported by this backend — use the Batch
    /// backend for that use case.
    /// </remarks>
    private static Dictionary<string, string> BuildEnvironment(
        ExecutionJobRecord job,
        CustomCodeJobInputs inputs,
        CustomCodeLocalBackendOptions options)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        // 1) Operator host allowlist FIRST, so controlled values below always win over it.
        foreach (var name in options.EnvironmentAllowlist)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null)
            {
                env[name] = value;
            }
        }

        // 2) Standard HONUA_* job markers (mirrors the cloud/local-process backends).
        env["HONUA_OPERATION_ID"] = job.OperationId;
        env["HONUA_WORKLOAD_NAME"] = job.Spec.WorkloadName;
        env["HONUA_JOB_KIND"] = job.Spec.Kind.ToString();
        if (!string.IsNullOrWhiteSpace(job.Spec.RuntimeProfile))
        {
            env["HONUA_RUNTIME_PROFILE"] = job.Spec.RuntimeProfile;
        }

        // 3) Custom-code contract variables — ONLY the known CUSTOMCODE_* names plus the job-scoped
        //    HONUA_BASE_URL — read from the submit-injected env.* spec keys. Arbitrary caller-supplied
        //    env.* keys are NOT surfaced: this is an allowlist keyed by contract name.
        //
        //    HONUA_JOB_TOKEN is deliberately EXCLUDED (F2, adversarial review): the raw scoped bearer
        //    token is never placed in the child's environment at all. See the remarks on this method.
        foreach (var envName in CustomCodeJobContract.ParameterToEnvName.Values)
        {
            CopyEnvParam(job.Spec.Parameters, envName, env);
        }

        CopyEnvParam(job.Spec.Parameters, CustomCodeJobContract.BaseUrlEnvName, env);

        // 4) Controlled PATH — the host PATH is never inherited. Always wins over an allowlisted "PATH".
        env["PATH"] = options.Path;

        return env;
    }

    private static void CopyEnvParam(
        IReadOnlyDictionary<string, string> specParameters,
        string envName,
        Dictionary<string, string> target)
    {
        var specKey = CustomCodeJobContract.ToEnvParamKey(envName);
        if (specParameters.TryGetValue(specKey, out var value) && value is not null)
        {
            target[envName] = value;
        }
    }

    private static string CreateScratchDirectory(string operationId, CustomCodeLocalBackendOptions options)
    {
        var root = string.IsNullOrWhiteSpace(options.WorkingRoot)
            ? Path.Combine(Path.GetTempPath(), "honua-customcode-local")
            : options.WorkingRoot;
        Directory.CreateDirectory(root);

        // A fresh, unique directory per job. The unique suffix means a stale directory from a prior
        // attempt of the same OperationId can never be reused, and the sanitized id can never contain a
        // separator. Containment under the root is asserted before use.
        var segment = $"{SandboxPaths.SanitizeSegment(operationId)}-{Guid.NewGuid():N}";
        var jobDirectory = SandboxPaths.ResolveContained(root, segment);
        Directory.CreateDirectory(jobDirectory);
        return jobDirectory;
    }

    private void EvictExecution(string operationId)
    {
        if (!_executions.TryRemove(operationId, out var execution))
        {
            return;
        }

        var scratch = execution.ScratchDirectory;
        execution.Dispose();
        TryDeleteDirectory(scratch, operationId);
    }

    private void TryDeleteDirectory(string? directory, string operationId)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.ScratchCleanupFailed(_logger, operationId, ex.Message);
        }
    }

    private static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled;

    private static BatchComputeSubmissionResult Failed(string message)
        => new() { Status = ExecutionJobStatus.Failed, Message = message };

    // Never surface a raw exception string (may carry a path or arg); keep the operator message stable.
    private static string SafeFailureMessage(Exception ex)
        => ex is CustomCodeWorkloadPreparationException or CustomCodePathEscapeException or InvalidOperationException
            ? ex.Message
            : "Local custom-code launch failed.";

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already exited, or a permission/race killing it; nothing more we can do in-process.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var execution in _executions.Values)
        {
            execution.RequestCancel();
        }

        _slots.Dispose();
    }

    /// <summary>In-process tracking state for one launched sandboxed process.</summary>
    private sealed class CustomCodeExecution(string operationId, string scratchDirectory, TimeSpan wallClock) : IDisposable
    {
        private readonly object _sync = new();
        private readonly Queue<string> _tail = new();
        private readonly CancellationTokenSource _cts = new();
        private ExecutionJobStatus _status = ExecutionJobStatus.Running;
        private string? _message;

        public string OperationId { get; } = operationId;

        public string ScratchDirectory { get; } = scratchDirectory;

        public TimeSpan WallClock { get; } = wallClock;

        public CancellationToken CancellationToken => _cts.Token;

        public void RequestCancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
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
                    ? "Custom-code process completed successfully."
                    : $"Custom-code process exited with code {exitCode.ToString(CultureInfo.InvariantCulture)}.{FormatTail()}";
            }

            DisposeToken();
        }

        public void CompleteTimedOut()
        {
            lock (_sync)
            {
                _status = ExecutionJobStatus.Failed;
                _message = $"Custom-code process exceeded the {WallClock.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s wall-clock limit and was killed.{FormatTail()}";
            }

            DisposeToken();
        }

        public void CompleteCancelled()
        {
            lock (_sync)
            {
                _status = ExecutionJobStatus.Cancelled;
                _message = "Custom-code process was cancelled.";
            }

            DisposeToken();
        }

        public void CompleteFailed()
        {
            lock (_sync)
            {
                _status = ExecutionJobStatus.Failed;
                _message = "Custom-code process monitoring failed.";
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

    private static partial class Log
    {
        [LoggerMessage(9310, LogLevel.Information, "Launched sandboxed custom-code process for job {OperationId} (pid {ProcessId}, wall-clock {WallClockSeconds}s)")]
        public static partial void ProcessLaunched(ILogger logger, string operationId, int processId, long wallClockSeconds);

        [LoggerMessage(9311, LogLevel.Information, "Custom-code process for job {OperationId} exited with code {ExitCode}")]
        public static partial void ProcessExited(ILogger logger, string operationId, int exitCode);

        [LoggerMessage(9312, LogLevel.Warning, "Custom-code process for job {OperationId} exceeded its {WallClockSeconds}s wall-clock limit and was killed")]
        public static partial void ProcessTimedOut(ILogger logger, string operationId, long wallClockSeconds);

        [LoggerMessage(9313, LogLevel.Information, "Custom-code process for job {OperationId} was cancelled")]
        public static partial void ProcessCancelled(ILogger logger, string operationId);

        [LoggerMessage(9314, LogLevel.Warning, "Local custom-code launch rejected for job {OperationId}: {Reason}")]
        public static partial void LaunchRejected(ILogger logger, string operationId, string reason);

        [LoggerMessage(9315, LogLevel.Warning, "Local custom-code launch failed for job {OperationId}: {Reason}")]
        public static partial void LaunchFailed(ILogger logger, string operationId, string reason);

        [LoggerMessage(9316, LogLevel.Debug, "Local custom-code pool saturated; deferring job {OperationId} (max {MaxConcurrent})")]
        public static partial void PoolSaturated(ILogger logger, string operationId, int maxConcurrent);

        [LoggerMessage(9317, LogLevel.Warning, "Local custom-code process for job {OperationId} is no longer tracked (provider id {ProviderOperationId})")]
        public static partial void ProcessLost(ILogger logger, string operationId, string providerOperationId);

        [LoggerMessage(9318, LogLevel.Warning, "Local custom-code monitor failed for job {OperationId}: {Reason}")]
        public static partial void ProcessMonitorFailed(ILogger logger, string operationId, string reason);

        [LoggerMessage(9319, LogLevel.Debug, "Failed to delete scratch directory for job {OperationId}: {Reason}")]
        public static partial void ScratchCleanupFailed(ILogger logger, string operationId, string reason);

        [LoggerMessage(9320, LogLevel.Warning, "Local custom-code backend was selected but is not enabled; failing job {OperationId} closed")]
        public static partial void BackendDisabled(ILogger logger, string operationId);

        [LoggerMessage(9321, LogLevel.Warning, "Local custom-code backend refused job {OperationId}: no SandboxUser configured and AcknowledgeUnconfinedExecutionRisk is not set; failing closed")]
        public static partial void UnconfinedExecutionRefused(ILogger logger, string operationId);
    }
}
