// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

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

    /// <summary>
    /// Builds the execution-job record the executor runs against. The durable
    /// <see cref="ExecutionJobSpec"/> is constructed through the SAME
    /// <see cref="GeoprocessingSpecBuilder"/> the production submit path
    /// (<c>GeoprocessingJobService.BuildSpec</c>) uses for the no-registered-workload
    /// case, from an equivalent single-step <c>AnalysisPlan</c> — so the spec a
    /// <c>gp run</c>/<c>gp plan</c> dry-run carries is byte-for-byte the spec a real
    /// single-step submit would produce for the same <c>(processId, inputs)</c>
    /// (issue #2180): same parameter bag (process-definitions, step-0 inputs, plan id),
    /// same Kind / TargetKind / Backend / WorkloadName, and the same data-driven
    /// <see cref="ExecutionJobSpec.RuntimeProfile"/> — <c>native</c> for a gdal.*
    /// executor, <c>null</c> (managed/default) otherwise. This makes "plan" a true dry
    /// run of the real spec, not a parallel representation.
    /// </summary>
    internal static ExecutionJobRecord BuildJobRecord(
        string processId,
        IReadOnlyDictionary<string, string> inputs,
        IProcessExecutor executor)
    {
        // The local runner has no process catalog to consult, so the required runtime
        // profile is derived from the resolved executor's accepted set instead — a
        // native (GDAL) executor accepts the native profile, which the submit path's
        // catalog lookup would stamp as the same `native` value; a managed executor
        // leaves the profile null/default, exactly as the submit path does.
        var requiredRuntimeProfile = executor.AcceptedRuntimeProfiles.Contains(RuntimeProfiles.Native)
            ? RuntimeProfiles.Native
            : null;

        var operationId = "local-" + Guid.NewGuid().ToString("N");

        // The local runner is a single-step (processId + step-0 inputs) invocation;
        // model it as the canonical single-step plan the shared spec builder consumes.
        var plan = GeoprocessingSpecBuilder.SingleStepPlan(processId, inputs, planId: operationId);
        var spec = GeoprocessingSpecBuilder.BuildNoWorkloadSpec(
            plan,
            new Dictionary<string, string>(StringComparer.Ordinal),
            requiredRuntimeProfile);

        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Running,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = spec,
        };
    }
}
