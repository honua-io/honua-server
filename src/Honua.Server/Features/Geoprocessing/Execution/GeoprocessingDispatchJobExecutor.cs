// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;

namespace Honua.Server.Features.Geoprocessing.Execution;

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
    private readonly FrozenDictionary<string, IJobExecutor> _handlers;
    private readonly ILogger<GeoprocessingDispatchJobExecutor> _logger;

    public GeoprocessingDispatchJobExecutor(
        GeometryBufferJobExecutor buffer,
        GeometryClipJobExecutor clip,
        GeometryIntersectJobExecutor intersect,
        GeometryProjectJobExecutor project,
        GeometryAreaJobExecutor area,
        GeometryUnionJobExecutor union,
        GeometryCentroidJobExecutor centroid,
        GeometryLengthJobExecutor length,
        GeometryConvexHullJobExecutor convexHull,
        GeometryDissolveJobExecutor dissolve,
        GeometrySimplifyJobExecutor simplify,
        GeometrySnapJobExecutor snap,
        AttributeRenameTransformExecutor attributeRename,
        AttributeCastTransformExecutor attributeCast,
        ComputedFieldTransformExecutor computedField,
        AttributeFilterTransformExecutor attributeFilter,
        SpatialFilterTransformExecutor spatialFilter,
        ClipTransformExecutor clip2,
        DedupTransformExecutor dedup,
        ILogger<GeoprocessingDispatchJobExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(intersect);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(union);
        ArgumentNullException.ThrowIfNull(centroid);
        ArgumentNullException.ThrowIfNull(length);
        ArgumentNullException.ThrowIfNull(convexHull);
        ArgumentNullException.ThrowIfNull(dissolve);
        ArgumentNullException.ThrowIfNull(simplify);
        ArgumentNullException.ThrowIfNull(snap);
        ArgumentNullException.ThrowIfNull(attributeRename);
        ArgumentNullException.ThrowIfNull(attributeCast);
        ArgumentNullException.ThrowIfNull(computedField);
        ArgumentNullException.ThrowIfNull(attributeFilter);
        ArgumentNullException.ThrowIfNull(spatialFilter);
        ArgumentNullException.ThrowIfNull(clip2);
        ArgumentNullException.ThrowIfNull(dedup);
        ArgumentNullException.ThrowIfNull(logger);

        _handlers = new Dictionary<string, IJobExecutor>(StringComparer.Ordinal)
        {
            [GeometryBufferJobExecutor.HandledProcessId] = buffer,
            [GeometryClipJobExecutor.HandledProcessId] = clip,
            [GeometryIntersectJobExecutor.HandledProcessId] = intersect,
            [GeometryProjectJobExecutor.HandledProcessId] = project,
            [GeometryAreaJobExecutor.HandledProcessId] = area,
            [GeometryUnionJobExecutor.HandledProcessId] = union,
            [GeometryCentroidJobExecutor.HandledProcessId] = centroid,
            [GeometryLengthJobExecutor.HandledProcessId] = length,
            [GeometryConvexHullJobExecutor.HandledProcessId] = convexHull,
            [GeometryDissolveJobExecutor.HandledProcessId] = dissolve,
            [GeometrySimplifyJobExecutor.HandledProcessId] = simplify,
            [GeometrySnapJobExecutor.HandledProcessId] = snap,
            [AttributeRenameTransformExecutor.HandledProcessId] = attributeRename,
            [AttributeCastTransformExecutor.HandledProcessId] = attributeCast,
            [ComputedFieldTransformExecutor.HandledProcessId] = computedField,
            [AttributeFilterTransformExecutor.HandledProcessId] = attributeFilter,
            [SpatialFilterTransformExecutor.HandledProcessId] = spatialFilter,
            [ClipTransformExecutor.HandledProcessId] = clip2,
            [DedupTransformExecutor.HandledProcessId] = dedup,
        }.ToFrozenDictionary(StringComparer.Ordinal);

        _logger = logger;
    }

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    /// <summary>
    /// Set of process ids the dispatcher currently routes. Surfaced so error
    /// messages can list the supported set without hard-coding it twice.
    /// </summary>
    internal IReadOnlyCollection<string> SupportedProcessIds => _handlers.Keys;

    public Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var processId = GeoprocessingDispatchHelper.ResolveProcessId(job.Spec.Parameters);

        if (string.IsNullOrWhiteSpace(processId)
            || !_handlers.TryGetValue(processId, out var handler))
        {
            var supported = string.Join(", ", _handlers.Keys.OrderBy(id => id, StringComparer.Ordinal));
            Log.UnsupportedProcessId(_logger, job.OperationId, processId ?? "<none>");
            return Task.FromResult(JobExecutionResult.Failed(
                $"Process id '{processId ?? "<none>"}' is not supported by the geoprocessing runtime. " +
                $"Supported ids in this slice: {supported}."));
        }

        return handler.ExecuteAsync(job, context, cancellationToken);
    }

    private static partial class Log
    {
        [LoggerMessage(9090, LogLevel.Warning,
            "Geoprocessing dispatch executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);
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
