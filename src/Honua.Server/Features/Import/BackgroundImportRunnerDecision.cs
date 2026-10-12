// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Import;

/// <summary>Where this host sends background file imports (honua-server#5916).</summary>
internal enum BackgroundImportRunnerMode
{
    /// <summary>Legacy in-process runner (<c>UniversalImportJobService</c>): long-lived host, no durable runner.</summary>
    InProcess,

    /// <summary>Durable execution job on the local backend, drained by the server's <c>JobExecutionService</c> loop.</summary>
    DurableLocal,

    /// <summary>Durable execution job on a remote batch backend (AWS Batch on the serverless cell).</summary>
    DurableRemote,

    /// <summary>A host frozen between requests (Lambda) with no durable runner: refuse with the typed 503.</summary>
    Unavailable,
}

/// <summary>
/// The background-import runner, resolved once per process from the same facts that decide
/// <c>jobs.runner</c>: the durable job substrate (Redis-backed store and queue) and the configured
/// execution workloads. A Lambda host (API behind the web adapter, or a control-plane event
/// handler) never runs imports in-process: it freezes after responding, so an accepted job would
/// never finish (honua-release run 38192851328).
/// </summary>
internal sealed class BackgroundImportRunnerDecision : IBackgroundImportRunnerInfo
{
    /// <summary>Reason code when a Lambda host has no durable runner to hand imports to.</summary>
    internal const string DurableRunnerRequiredReason = "durable-runner-required";

    /// <summary>
    /// Reason code when a batch runner is configured but file storage is the host-local provider,
    /// so a batch worker on another machine could not read the staged source.
    /// </summary>
    internal const string SharedFileStorageRequiredReason = "shared-file-storage-required";

    /// <summary>Reason code when no import job service is composed on this host.</summary>
    internal const string ImportServiceMissingReason = "dependency-unavailable";

    /// <summary>Set by the AWS Lambda runtime in every function (including web-adapter functions).</summary>
    internal const string AwsLambdaFunctionNameKey = "AWS_LAMBDA_FUNCTION_NAME";

    /// <summary>Workload name stamped on local durable import jobs.</summary>
    internal const string LocalWorkloadNamePrefix = "file-import:";

    internal BackgroundImportRunnerDecision(
        BackgroundImportRunnerMode mode,
        ExecutionJobDefinition? workload = null,
        string? unavailableReason = null)
    {
        if (mode == BackgroundImportRunnerMode.DurableRemote)
        {
            ArgumentNullException.ThrowIfNull(workload);
        }

        Mode = mode;
        Workload = workload;
        UnavailableReason = mode == BackgroundImportRunnerMode.Unavailable
            ? unavailableReason ?? DurableRunnerRequiredReason
            : null;
    }

    public BackgroundImportRunnerMode Mode { get; }

    /// <summary>The remote workload a <see cref="BackgroundImportRunnerMode.DurableRemote"/> job is routed to.</summary>
    public ExecutionJobDefinition? Workload { get; }

    public string Runner => Mode switch
    {
        BackgroundImportRunnerMode.DurableRemote => ImportJobRunners.Batch,
        BackgroundImportRunnerMode.Unavailable => ImportJobRunners.Unavailable,
        _ => ImportJobRunners.InProcess,
    };

    public bool AcceptsBackgroundImports => Mode != BackgroundImportRunnerMode.Unavailable;

    public string? UnavailableReason { get; }

    /// <summary>Whether imports become durable execution jobs.</summary>
    public bool IsDurable => Mode is BackgroundImportRunnerMode.DurableLocal or BackgroundImportRunnerMode.DurableRemote;

    /// <summary>
    /// The pure decision: a remote workload wins whenever the durable substrate is available; a
    /// Lambda host without one refuses; a long-lived host uses the local durable queue when the
    /// substrate is available and the in-process runner otherwise.
    /// </summary>
    public static BackgroundImportRunnerDecision Decide(
        bool durableRuntimeAvailable,
        ExecutionJobDefinition? remoteWorkload,
        bool frozenBetweenRequests,
        bool sharedFileStorage = true)
    {
        if (durableRuntimeAvailable && remoteWorkload is not null && sharedFileStorage)
        {
            return new(BackgroundImportRunnerMode.DurableRemote, remoteWorkload);
        }

        if (frozenBetweenRequests)
        {
            return new(
                BackgroundImportRunnerMode.Unavailable,
                unavailableReason: durableRuntimeAvailable && remoteWorkload is not null
                    ? SharedFileStorageRequiredReason
                    : DurableRunnerRequiredReason);
        }

        return durableRuntimeAvailable
            ? new(BackgroundImportRunnerMode.DurableLocal)
            : new(BackgroundImportRunnerMode.InProcess);
    }

    /// <summary>
    /// A host that the platform freezes between requests: any AWS Lambda function, which covers the
    /// API behind the Lambda Web Adapter and the control-plane event handlers
    /// (<c>HONUA_CONTROL_PLANE_LAMBDA_HANDLER</c>).
    /// </summary>
    public static bool IsFrozenBetweenRequestsHost(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !string.IsNullOrWhiteSpace(configuration[AwsLambdaFunctionNameKey])
            || ControlPlaneLambdaConnectionRelease.IsEventHandlerProfile(configuration);
    }

    /// <summary>
    /// Selects the remote workload for imports: an explicit <see cref="ExecutionJobKind.FileImport"/>
    /// workload first, otherwise the managed geoprocessing workload (the same generic server image,
    /// whose execution-worker profile composes the file-import executor). Only remote backends that
    /// are registered on this host qualify; custom-code and native profiles never do.
    /// </summary>
    public static ExecutionJobDefinition? SelectRemoteWorkload(
        IEnumerable<ExecutionJobDefinition> definitions,
        IEnumerable<IBatchComputeBackend> backends)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(backends);

        var registered = backends.ToArray();
        var candidates = definitions
            .Where(definition =>
                !string.Equals(definition.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal)
                && registered.Resolve(definition.Backend, definition.TargetKind) is not null
                && RuntimeProfiles.CanClaim(RuntimeProfiles.DefaultAccepted, definition.RuntimeProfile))
            .ToArray();

        return candidates.FirstOrDefault(static definition => definition.Kind == ExecutionJobKind.FileImport)
            ?? candidates.FirstOrDefault(static definition => definition.Kind == ExecutionJobKind.Geoprocessing);
    }

    /// <summary>Resolves the decision from the composed host.</summary>
    public static BackgroundImportRunnerDecision Resolve(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var substrate = services.GetService<IOptions<DurableJobSubstrateOptions>>()?.Value ?? new DurableJobSubstrateOptions();
        var durable = substrate.Classify(
            services.GetService<IExecutionJobStore>() is not null,
            services.GetService<IJobQueue>() is not null) == DurableJobSubstrateCause.Available;

        var controlPlane = services.GetService<IOptionsMonitor<ControlPlaneOptions>>()?.CurrentValue;
        var remoteWorkload = durable && controlPlane is not null
            ? SelectRemoteWorkload(
                ConfigurationExecutionJobDefinitionRegistry.BuildDefinitions(controlPlane),
                services.GetServices<IBatchComputeBackend>())
            : null;

        var configuration = services.GetService<IConfiguration>();
        var frozen = configuration is not null && IsFrozenBetweenRequestsHost(configuration);
        var fileStorage = services.GetService<ICloudFileStorage>();
        var sharedFileStorage = fileStorage is not null && fileStorage.Provider != CloudStorageProvider.Local;
        return Decide(durable, remoteWorkload, frozen, sharedFileStorage);
    }
}
