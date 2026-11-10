// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Diagnostics;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.ControlPlane;
using Honua.Geoprocessing;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing.Execution;

internal sealed partial class GeoprocessingDispatchJobExecutor : IJobExecutor
{
    private readonly FrozenDictionary<string, IProcessExecutor> _handlers;
    private readonly ILogger<GeoprocessingDispatchJobExecutor> _logger;
    private readonly IProcessUsageTelemetry? _usageTelemetry;
    private readonly IServiceScopeFactory? _serviceScopeFactory;
    private readonly IGeoprocessingOutputObjectStore? _outputStore;
    private readonly IExecutionJobStore? _jobStore;
    private readonly IOptionsMonitor<GeoprocessingOutputStagingOptions>? _stagingOptions;

    // The dispatcher is a singleton, so the scoped workspace service is resolved per dispatch and cannot be constructor-injected.
    public GeoprocessingDispatchJobExecutor(
        IEnumerable<IProcessExecutor> executors,
        ILogger<GeoprocessingDispatchJobExecutor> logger,
        IProcessUsageTelemetry? usageTelemetry = null,
        IServiceScopeFactory? serviceScopeFactory = null,
        IGeoprocessingOutputObjectStore? outputStore = null,
        IExecutionJobStore? jobStore = null,
        IOptionsMonitor<GeoprocessingOutputStagingOptions>? stagingOptions = null)
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
        _outputStore = outputStore;
        _jobStore = jobStore;
        _stagingOptions = stagingOptions;
    }

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

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

        // Establish the job-security scope for the whole dispatch (#3068). The worker has no
        // HttpContext, so the request-scoped RLS and field-mask sources would otherwise resolve
        // NO row filter and an EMPTY mask and hand the submitter data they cannot see
        // synchronously. Begun here — the single seam every managed process executor runs
        // through — rather than per-executor, so a newly added layer-sourced process inherits it.
        //
        // The scope is begun even when the record carries no snapshot: that combination is the
        // fail-closed signal the catalog-layer read seam refuses on, rather than reading
        // unrestricted. Begun synchronously in this method's own flow so the AsyncLocal write is
        // visible to the awaited executor.
        using var securityScope = JobSecurityScope.Begin(job.Audit.SubmitterSecurityContext);

        JobExecutionResult result;
        var (workspaceScope, effectiveContext, workspaceError, workspaceErrorIsRetryable) =
            await TryResolveWorkspaceRoutingAsync(job, context, cancellationToken).ConfigureAwait(false);
        using (workspaceScope)
        {
            if (workspaceError is not null)
            {
                Log.WorkspaceProviderUnavailable(_logger, job.OperationId, workspaceError);
                activity?.SetStatus(ActivityStatusCode.Error, "env:workspace could not be resolved");
                return JobExecutionResult.Failed(workspaceError) with { IsRetryable = workspaceErrorIsRetryable };
            }

            try
            {
                var streamContext = new ManagedFeatureStreamContext(effectiveContext, job, _outputStore, _jobStore,
                    _stagingOptions?.CurrentValue ?? new GeoprocessingOutputStagingOptions(), _serviceScopeFactory);
                result = await handler.ExecuteAsync(job, streamContext, cancellationToken).ConfigureAwait(false);
            }
            catch (TransformInputException ex)
            {
                return JobExecutionResult.Failed(ex.PublicMessage) with { IsRetryable = false };
            }
            catch (ArtifactAlreadyExistsException ex)
            {
                Log.OutputCollision(_logger, job.OperationId, ex.Message);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                return JobExecutionResult.Failed(ex.Message) with { IsRetryable = false };
            }
            catch (WorkspaceQuotaExceededException ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                return JobExecutionResult.Failed(ex.Message) with { IsRetryable = false };
            }
            catch (ArtifactReplacementFailedException ex)
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

    // env:workspace is a label, not a workspace id, and artifact writes require the resolved id.
    private async Task<(IDisposable? Scope, IJobExecutionContext EffectiveContext, string? Error, bool IsRetryable)>
        TryResolveWorkspaceRoutingAsync(
            ExecutionJobRecord job,
            IJobExecutionContext context,
            CancellationToken cancellationToken)
    {
        var requestedLabel = job.Spec.Parameters.GetValueOrDefault(GeoprocessingProtocolMetadataKeys.GPServerWorkspace);
        if (string.IsNullOrWhiteSpace(requestedLabel))
        {
            return (null, context, null, true);
        }

        var scope = _serviceScopeFactory?.CreateScope();
        var workspaceLifecycle = scope?.ServiceProvider.GetService<IWorkspaceLifecycleService>();
        if (workspaceLifecycle is null)
        {
            scope?.Dispose();
            return (null, context,
                $"env:workspace='{requestedLabel}' was requested but no workspace storage provider is configured for this deployment.", false);
        }

        var workspaceOwner = job.Audit.SubmitterSecurityContext?.WorkspaceOwnerId ?? job.Audit.RequestedBy;
        var ownerId = string.IsNullOrWhiteSpace(workspaceOwner) ? "anonymous" : workspaceOwner;

        Workspace resolvedWorkspace;
        try
        {
            var scopeId = job.Audit.SubmitterSecurityContext?.TenantId;
            resolvedWorkspace = scopeId is null
                ? await workspaceLifecycle.GetOrCreateNamedWorkspaceAsync(ownerId, requestedLabel, cancellationToken).ConfigureAwait(false)
                : await workspaceLifecycle.GetOrCreateScopedWorkspaceAsync(ownerId, requestedLabel, scopeId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            scope?.Dispose();
            throw;
        }
        catch (WorkspaceQuotaExceededException ex)
        {
            scope?.Dispose();
            return (null, context, ex.Message, false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentionally broad: workspace resolution can fail for many storage-provider
            // reasons (I/O, auth, quota) and must surface as a job failure rather than crash
            // the dispatcher. The raw exception is logged for diagnosis; the returned reason
            // stays provider-detail-free so it is safe to surface on the job status.
            scope?.Dispose();
            Log.WorkspaceResolutionFailed(_logger, job.OperationId, requestedLabel, ex);
            return (null, context, $"env:workspace='{requestedLabel}' could not be resolved.", true);
        }

        var overwrite =
            job.Spec.Parameters.TryGetValue(GeoprocessingProtocolMetadataKeys.GPServerOverwriteOutput, out var raw)
            && bool.TryParse(raw, out var parsedOverwrite)
            && parsedOverwrite;

        var effectiveContext = new WorkspaceRoutingJobExecutionContext(
            context, job, resolvedWorkspace.WorkspaceId, overwrite, workspaceLifecycle);
        return (scope, effectiveContext, null, true);
    }

    private static partial class Log
    {
        [LoggerMessage(9090, LogLevel.Warning,
            "Geoprocessing dispatch executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9091, LogLevel.Warning,
            "Geoprocessing dispatch executor failed job {OperationId}: {Reason}")]
        public static partial void WorkspaceProviderUnavailable(ILogger logger, string operationId, string reason);

        [LoggerMessage(9093, LogLevel.Warning,
            "Geoprocessing dispatch executor failed to resolve env:workspace='{RequestedLabel}' for job {OperationId}")]
        public static partial void WorkspaceResolutionFailed(ILogger logger, string operationId, string requestedLabel, Exception exception);

        [LoggerMessage(9092, LogLevel.Warning,
            "Geoprocessing dispatch executor failed job {OperationId}: output collision — {Reason}")]
        public static partial void OutputCollision(ILogger logger, string operationId, string reason);
    }
}

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
