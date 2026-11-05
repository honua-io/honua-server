// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Geoprocessing.CustomCode;

// A custom-code job claimed on this process fails instead of running untrusted code in the server.
internal sealed partial class CustomCodeDispatchJobExecutor : IJobExecutor
{
    private static readonly IReadOnlySet<string> CustomCodeProfileSet =
        new HashSet<string>(StringComparer.Ordinal) { CustomCodeJobContract.RuntimeProfile };

    private readonly ILogger<CustomCodeDispatchJobExecutor> _logger;

    public CustomCodeDispatchJobExecutor(ILogger<CustomCodeDispatchJobExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    public IReadOnlySet<string> AcceptedRuntimeProfiles => CustomCodeProfileSet;

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
