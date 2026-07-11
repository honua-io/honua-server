// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Diagnostics;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.ControlPlane;
using Honua.Geoprocessing;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Production <see cref="IJobExecutor"/> for <see cref="ExecutionJobKind.Geoprocessing"/>
/// jobs. Acts as a per-process-id dispatcher over the registered geometry
/// executors so the worker host can register a single executor per
/// <see cref="ExecutionJobKind"/> while still surfacing per-process behavior.
///
/// Slice 1 (#1031) introduced <see cref="GeometryBufferJobExecutor"/> as the
/// only registered executor; submitting any other process id surfaced the
/// "first-slice geoprocessing runtime" error from inside that class. Slice 2
/// added <c>geometry.clip</c>, <c>geometry.intersect</c>, and
/// <c>geometry.project</c> as additional per-process executors. Slice 3
/// added <c>geometry.area</c> (per-feature measure) and <c>geometry.union</c>
/// (collection aggregation). Slice 4 added <c>geometry.centroid</c>,
/// <c>geometry.length</c>, and <c>geometry.convex-hull</c> — finishing the
/// deterministic single-feature vector set. Slice 5 rounds out the
/// migration-priority vector ops with <c>geometry.dissolve</c>
/// (group-aware aggregate), <c>geometry.simplify</c> (Douglas-Peucker),
/// and <c>geometry.snap</c> (vertex conditioning to a reference
/// geometry). This dispatcher routes between them and emits a single,
/// consistent "unsupported process id" error for everything outside the
/// current supported set.
/// </summary>
internal sealed partial class GeoprocessingDispatchJobExecutor : IJobExecutor
{
    private readonly FrozenDictionary<string, IProcessExecutor> _handlers;
    private readonly ILogger<GeoprocessingDispatchJobExecutor> _logger;
    private readonly IProcessUsageTelemetry? _usageTelemetry;
    private readonly IServiceScopeFactory? _serviceScopeFactory;

    /// <summary>
    /// Composes the dispatcher over the auto-registered per-process executors
    /// (issue #2122). Each <see cref="IProcessExecutor"/> self-declares the process
    /// ids it handles through <see cref="IProcessExecutor.ProcessIds"/>, so the
    /// routing table is built from a single DI scan instead of a hand-maintained
    /// constructor naming every executor. Duplicate or empty ids fail fast at
    /// composition time via <see cref="ProcessExecutorRouteTable.Build"/>.
    /// <paramref name="serviceScopeFactory"/> resolves the scoped
    /// <see cref="IWorkspaceLifecycleService"/> per dispatch for GP
    /// <c>env:workspace</c>/<c>env:overwriteOutput</c> — the dispatcher itself is
    /// a singleton, so the scoped service cannot be constructor-injected
    /// directly. Optional so existing test call sites that construct the
    /// dispatcher without DI keep compiling; when omitted (or when no workspace
    /// storage provider is registered), <c>env:workspace</c> jobs fail fast with
    /// a clear "no workspace provider configured" message instead of silently
    /// ignoring the request.
    /// </summary>
    public GeoprocessingDispatchJobExecutor(
        IEnumerable<IProcessExecutor> executors,
        ILogger<GeoprocessingDispatchJobExecutor> logger,
        IProcessUsageTelemetry? usageTelemetry = null,
        IServiceScopeFactory? serviceScopeFactory = null)
    {
        ArgumentNullException.ThrowIfNull(executors);
        ArgumentNullException.ThrowIfNull(logger);

        // The remote DAG source connectors (source.honua-layer/esri-featureserver/
        // ogc-features/wfs/postgis) self-register as IProcessExecutor instances, so the
        // single route-table scan picks them up alongside every other per-process executor.
        _handlers = ProcessExecutorRouteTable.Build(executors);
        _logger = logger;
        _usageTelemetry = usageTelemetry;
        _serviceScopeFactory = serviceScopeFactory;
    }

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    /// <summary>
    /// Set of process ids the dispatcher currently routes. Surfaced so error
    /// messages can list the supported set without hard-coding it twice.
    /// </summary>
    internal IReadOnlyCollection<string> SupportedProcessIds => _handlers.Keys;

    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var processId = GeoprocessingDispatchHelper.ResolveProcessId(job.Spec.Parameters);

        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.GeoprocessingDispatch, ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.JobId, job.OperationId);
        activity?.SetTag("honua.gp.process_id", processId ?? "<none>");
        activity?.SetTag("honua.gp.backend", job.Spec.Backend);

        if (string.IsNullOrWhiteSpace(processId)
            || !_handlers.TryGetValue(processId, out var handler))
        {
            var supported = string.Join(", ", _handlers.Keys.OrderBy(id => id, StringComparer.Ordinal));
            Log.UnsupportedProcessId(_logger, job.OperationId, processId ?? "<none>");
            activity?.SetStatus(ActivityStatusCode.Error, "Unsupported process id");
            return JobExecutionResult.Failed(
                $"Process id '{processId ?? "<none>"}' is not supported by the geoprocessing runtime. " +
                $"Supported ids in this slice: {supported}.");
        }

        JobExecutionResult result;
        using (var workspaceScope = TryResolveWorkspaceRouting(job, context, out var effectiveContext, out var workspaceError))
        {
            if (workspaceError is not null)
            {
                Log.WorkspaceProviderUnavailable(_logger, job.OperationId, workspaceError);
                activity?.SetStatus(ActivityStatusCode.Error, "env:workspace requested with no workspace provider");
                return JobExecutionResult.Failed(workspaceError);
            }

            try
            {
                result = await handler.ExecuteAsync(job, effectiveContext, cancellationToken).ConfigureAwait(false);
            }
            catch (ArtifactAlreadyExistsException ex)
            {
                Log.OutputCollision(_logger, job.OperationId, ex.Message);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                return JobExecutionResult.Failed(ex.Message);
            }
        }

        if (result.Status != ExecutionJobStatus.Succeeded)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.ErrorMessage);
        }

        // Usage-ranked tiering (#2144): record every dispatched invocation with its
        // outcome. Recording never throws, so it cannot affect the job result.
        _usageTelemetry?.RecordInvocation(processId, result.Status == ExecutionJobStatus.Succeeded);

        return result;
    }

    /// <summary>
    /// Resolves GP <c>env:workspace</c> routing for this dispatch, if requested.
    /// Returns the <see cref="IServiceScope"/> the caller must dispose after the
    /// executor runs (or <c>null</c> when no scope was opened), and reports
    /// either the wrapped context to use or a clear error when a workspace was
    /// requested but no storage provider is configured. Isolated to a single
    /// method so every one of the ~30 per-process executors gets workspace
    /// routing "for free" through the context they already call
    /// <c>PublishArtifactAsync</c> on — no executor needs its own workspace logic.
    /// </summary>
    private IDisposable? TryResolveWorkspaceRouting(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        out IJobExecutionContext effectiveContext,
        out string? workspaceError)
    {
        effectiveContext = context;
        workspaceError = null;

        var workspaceId = job.Spec.Parameters.GetValueOrDefault(GeoprocessingProtocolMetadataKeys.GPServerWorkspace);
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return null;
        }

        var scope = _serviceScopeFactory?.CreateScope();
        var workspaceLifecycle = scope?.ServiceProvider.GetService<IWorkspaceLifecycleService>();
        if (workspaceLifecycle is null)
        {
            scope?.Dispose();
            workspaceError =
                $"env:workspace='{workspaceId}' was requested but no workspace storage provider is configured for this deployment.";
            return null;
        }

        var overwrite =
            job.Spec.Parameters.TryGetValue(GeoprocessingProtocolMetadataKeys.GPServerOverwriteOutput, out var raw)
            && bool.TryParse(raw, out var parsedOverwrite)
            && parsedOverwrite;

        effectiveContext = new WorkspaceRoutingJobExecutionContext(
            context, job, workspaceId, overwrite, workspaceLifecycle);
        return scope;
    }

    private static partial class Log
    {
        [LoggerMessage(9090, LogLevel.Warning,
            "Geoprocessing dispatch executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9091, LogLevel.Warning,
            "Geoprocessing dispatch executor failed job {OperationId}: {Reason}")]
        public static partial void WorkspaceProviderUnavailable(ILogger logger, string operationId, string reason);

        [LoggerMessage(9092, LogLevel.Warning,
            "Geoprocessing dispatch executor failed job {OperationId}: output collision — {Reason}")]
        public static partial void OutputCollision(ILogger logger, string operationId, string reason);
    }
}

/// <summary>
/// Shared helper used by the dispatcher and the per-process executors to
/// extract the canonical process id from the durable spec parameter bag.
/// Keeps lookup semantics identical across the slice 2 executors.
/// </summary>
internal static class GeoprocessingDispatchHelper
{
    public static string? ResolveProcessId(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (parameters.TryGetValue(ExecutionJobParameterKeys.GeoprocessingProcessDefinitions, out var raw)
            && !string.IsNullOrWhiteSpace(raw))
        {
            var first = raw
                .Split(
                    ExecutionJobParameterKeys.MetadataListSeparator,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                return first;
            }
        }

        if (parameters.TryGetValue("protocolProcessId", out var protocolProcessId)
            && !string.IsNullOrWhiteSpace(protocolProcessId))
        {
            return protocolProcessId;
        }

        if (parameters.TryGetValue(GeoprocessingProtocolMetadataKeys.GPServerTaskName, out var gpTask)
            && !string.IsNullOrWhiteSpace(gpTask))
        {
            return gpTask;
        }

        return null;
    }
}
