// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Native-profile dispatcher for <see cref="ExecutionJobKind.Geoprocessing"/>
/// jobs inside the GDAL worker image. Mirrors the lean
/// <c>GeoprocessingDispatchJobExecutor</c> pattern: the worker host registers a
/// single <see cref="IJobExecutor"/> per <see cref="ExecutionJobKind"/>, and this
/// dispatcher routes a claimed job to the matching GDAL-backed handler by process
/// id.
///
/// It overrides <see cref="AcceptedRuntimeProfiles"/> to <c>{ "native" }</c> so the
/// substrate claim fence (this branch's <see cref="RuntimeProfiles"/> helper) keeps
/// the lean managed worker from claiming native-profile jobs and keeps this worker
/// from claiming managed-profile jobs.
/// </summary>
internal sealed partial class GdalDispatchJobExecutor : IJobExecutor
{
    private static readonly IReadOnlySet<string> NativeProfileSet =
        new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

    private readonly FrozenDictionary<string, IProcessExecutor> _handlers;
    private readonly ILogger<GdalDispatchJobExecutor> _logger;

    /// <summary>
    /// Composes the dispatcher over the auto-registered GDAL-backed executors
    /// (issue #2122). Each <see cref="IProcessExecutor"/> self-declares the process
    /// ids it handles through <see cref="IProcessExecutor.ProcessIds"/> — including
    /// the <c>surface.*</c> and <c>raster.statistics/histogram</c> families that fan
    /// a single executor over several ids — so the O(1) routing table is built from
    /// a single DI scan via <see cref="ProcessExecutorRouteTable.Build"/> instead of
    /// a hand-maintained constructor naming every executor and its id set.
    /// </summary>
    public GdalDispatchJobExecutor(
        IEnumerable<IProcessExecutor> executors,
        ILogger<GdalDispatchJobExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(executors);
        ArgumentNullException.ThrowIfNull(logger);

        _handlers = ProcessExecutorRouteTable.Build(executors);
        _logger = logger;
    }

    /// <inheritdoc />
    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    /// <inheritdoc />
    public IReadOnlySet<string> AcceptedRuntimeProfiles => NativeProfileSet;

    /// <summary>Process ids this dispatcher can route.</summary>
    public IReadOnlyCollection<string> SupportedProcessIds => _handlers.Keys;

    /// <inheritdoc />
    public Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var processId = GdalJobInputReader.ResolveProcessId(job.Spec.Parameters);

        if (string.IsNullOrWhiteSpace(processId) || !_handlers.TryGetValue(processId, out var handler))
        {
            var supported = string.Join(", ", _handlers.Keys.OrderBy(id => id, StringComparer.Ordinal));
            Log.UnsupportedProcessId(_logger, job.OperationId, processId ?? "<none>");
            return Task.FromResult(JobExecutionResult.Failed(
                $"Process id '{processId ?? "<none>"}' is not supported by the GDAL worker runtime. " +
                $"Supported ids: {supported}."));
        }

        return handler.ExecuteAsync(job, context, cancellationToken);
    }

    private static partial class Log
    {
        [LoggerMessage(9230, LogLevel.Warning,
            "GDAL dispatch executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);
    }
}
