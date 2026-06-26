// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;

namespace Honua.Geoprocessing.LocalRunner;

/// <summary>
/// Headless, single-process geoprocessing runner (GP Devkit, issue #2123). Invokes
/// exactly ONE registered <see cref="IProcessExecutor"/> directly against in-memory
/// inputs — with NO Redis, NO durable job store, NO queue, and NO control plane.
///
/// <para>
/// It reuses the real executor path: it builds the same durable
/// <see cref="ExecutionJobSpec.Parameters"/> bag the production submit path projects
/// (the <c>geoprocessing.process_definitions</c> id key plus
/// <c>geoprocessing.step.0.&lt;name&gt;</c> step inputs), constructs a minimal
/// in-memory <see cref="IJobExecutionContext"/>, runs the executor, and returns the
/// captured artifact reference(s), structured logs, warnings, status, and wall-clock
/// timing. Validation and artifact-size-cap failures surface as a failed result with
/// the executor's own classified message — exactly as they would in production.
/// </para>
///
/// <para>
/// The runner is intentionally agnostic to the executor family: the same code path
/// drives the managed NTS geometry/transform/analytics executors and the native
/// GDAL-backed executors (whose CLI command is surfaced into the captured logs). It
/// performs no native work itself, so a managed op runs fully offline; a GDAL op runs
/// offline too when its CLI runner seam is faked.
/// </para>
/// </summary>
public sealed class GeoprocessingLocalRunner
{
    private readonly IReadOnlyDictionary<string, IProcessExecutor> _executorsById;

    /// <summary>
    /// Creates a runner over the supplied executor set. Typically resolved from DI as
    /// <c>IEnumerable&lt;IProcessExecutor&gt;</c> (the same registrations the worker host
    /// uses), but any explicit set works for tests and the dev CLI.
    /// </summary>
    /// <param name="executors">The per-process executors the runner can invoke.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when two executors declare the same process id (the same fail-fast
    /// contract the production dispatchers enforce).
    /// </exception>
    public GeoprocessingLocalRunner(IEnumerable<IProcessExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(executors);
        _executorsById = ProcessExecutorRouteTable.Build(executors);
    }

    /// <summary>
    /// The set of process ids this runner can invoke, ordinally sorted.
    /// </summary>
    public IReadOnlyList<string> AvailableProcessIds =>
        _executorsById.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Runs a single process against the supplied step-0 inputs.
    /// </summary>
    /// <param name="processId">Canonical dotted process id (e.g. <c>geometry.buffer</c>).</param>
    /// <param name="inputs">
    /// Step-0 input name/value pairs (e.g. <c>wkb</c>, <c>srid</c>, <c>distance</c>),
    /// projected under the canonical <c>geoprocessing.step.0.&lt;name&gt;</c> keys the
    /// executors read. Values are opaque strings, matching the durable spec contract.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The structured outcome of the run.</returns>
    public async Task<LocalRunResult> RunAsync(
        string processId,
        IReadOnlyDictionary<string, string> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        ArgumentNullException.ThrowIfNull(inputs);

        if (!_executorsById.TryGetValue(processId, out var executor))
        {
            var supported = string.Join(", ", AvailableProcessIds);
            return LocalRunResult.UnknownProcess(processId, supported);
        }

        var record = BuildJobRecord(processId, inputs, executor);
        var context = new LocalJobExecutionContext(record.OperationId);

        var stopwatch = Stopwatch.StartNew();
        JobExecutionResult result;
        try
        {
            result = await executor.ExecuteAsync(record, context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new LocalRunResult
            {
                ProcessId = processId,
                Status = ExecutionJobStatus.Failed,
                ErrorMessage = $"Executor threw {ex.GetType().Name}: {ex.Message}",
                Artifacts = context.Artifacts,
                Logs = context.Logs,
                Warnings = [],
                Elapsed = stopwatch.Elapsed,
            };
        }

        stopwatch.Stop();

        return new LocalRunResult
        {
            ProcessId = processId,
            Status = result.Status,
            ErrorMessage = result.ErrorMessage,
            Artifacts = context.Artifacts,
            Logs = context.Logs,
            Warnings = result.Warnings,
            Elapsed = stopwatch.Elapsed,
        };
    }

    private static ExecutionJobRecord BuildJobRecord(
        string processId,
        IReadOnlyDictionary<string, string> inputs,
        IProcessExecutor executor)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // The canonical process-id key the dispatch helper / executors resolve.
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = processId,
            // protocolProcessId is the fallback key several executors also accept.
            ["protocolProcessId"] = processId,
        };

        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        foreach (var (name, value) in inputs)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            parameters[prefix + name] = value ?? string.Empty;
        }

        // Stamp the runtime profile the executor accepts so a native (GDAL) executor's
        // own profile guard, if any, is satisfied; managed executors ignore it.
        var runtimeProfile = executor.AcceptedRuntimeProfiles.Contains(RuntimeProfiles.Native)
            ? RuntimeProfiles.Native
            : RuntimeProfiles.Managed;

        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = "local-" + Guid.NewGuid().ToString("N"),
            Status = ExecutionJobStatus.Running,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "gp-local:" + processId,
                RuntimeProfile = runtimeProfile,
                Parameters = parameters,
            },
        };
    }
}
