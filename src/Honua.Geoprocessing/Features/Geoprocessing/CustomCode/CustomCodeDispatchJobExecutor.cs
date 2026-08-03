// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Geoprocessing.CustomCode;

/// <summary>
/// The thin server-side dispatch executor for the <c>custom-code</c> runtime
/// profile. It exists primarily as the worker-side half of the runtime-profile
/// claim fence: by accepting <em>only</em> the <c>custom-code</c> profile it keeps
/// the lean managed <c>GeoprocessingDispatchJobExecutor</c> and the native GDAL
/// worker from ever claiming a custom-code job (mirroring the GDAL worker's
/// <c>GdalDispatchJobExecutor</c> fence).
/// </summary>
/// <remarks>
/// The heavy work — cloning the pinned commit, installing dependencies, and running
/// the user entrypoint — happens IN the custom-code Batch container (the harness,
/// built separately), reached by the param-driven remote-backend submission at
/// submit time (analogous to how the GP Batch workload is submitted through
/// <c>AwsBatchComputeBackend</c>). A custom-code job therefore never executes user
/// code in-process here. If a job carrying this profile is ever claimed locally
/// (i.e. the deployment failed to configure a custom-code Batch workload so the job
/// was not routed to a remote backend), this executor fails it loudly rather than
/// silently no-op'ing or running untrusted code in the server process.
/// </remarks>
internal sealed partial class CustomCodeDispatchJobExecutor : IJobExecutor
{
    private readonly ILogger<CustomCodeDispatchJobExecutor> _logger;

    /// <summary>Creates the custom-code dispatch executor.</summary>
    public CustomCodeDispatchJobExecutor(ILogger<CustomCodeDispatchJobExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    /// <inheritdoc />
    public IReadOnlySet<string> AcceptedRuntimeProfiles => RuntimeProfiles.CustomCodeAccepted;

    /// <inheritdoc />
    public Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        // Reaching this point means a custom-code job was claimed by an in-process
        // worker instead of being dispatched to the custom-code Batch workload. That
        // is a deployment misconfiguration: custom (untrusted) user code must run in
        // the isolated Batch container, never in the server process. Fail closed.
        Log.CustomCodeNotDispatchedToBatch(_logger, job.OperationId);
        return Task.FromResult(JobExecutionResult.Failed(
            "Custom-code jobs must be dispatched to the custom-code Batch workload and cannot run in-process. " +
            "Configure a ControlPlane execution workload with RuntimeProfile='custom-code'."));
    }

    private static partial class Log
    {
        [LoggerMessage(9240, LogLevel.Error,
            "Custom-code job {OperationId} was claimed in-process but custom-code must run in the Batch container; failing the job")]
        public static partial void CustomCodeNotDispatchedToBatch(ILogger logger, string operationId);
    }
}
