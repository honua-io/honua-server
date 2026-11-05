// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

internal enum BackgroundImportRunnerMode
{
    InProcess,

    DurableLocal,

    DurableRemote,

    Unavailable,
}

// A frozen host never runs imports in-process, because it stops after responding and the accepted job would never finish.
internal sealed class BackgroundImportRunnerDecision : IBackgroundImportRunnerInfo
{
    internal const string DurableRunnerRequiredReason = "durable-runner-required";

    internal const string SharedFileStorageRequiredReason = "shared-file-storage-required";

    internal const string ImportServiceMissingReason = "dependency-unavailable";

    internal const string AwsLambdaFunctionNameKey = "AWS_LAMBDA_FUNCTION_NAME";

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

    public ExecutionJobDefinition? Workload { get; }

    public string Runner => Mode switch
    {
        BackgroundImportRunnerMode.DurableRemote => ImportJobRunners.Batch,
        BackgroundImportRunnerMode.Unavailable => ImportJobRunners.Unavailable,
        _ => ImportJobRunners.InProcess,
    };

    public bool AcceptsBackgroundImports => Mode != BackgroundImportRunnerMode.Unavailable;

    public string? UnavailableReason { get; }

    public bool IsDurable => Mode is BackgroundImportRunnerMode.DurableLocal or BackgroundImportRunnerMode.DurableRemote;

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

    public static bool IsFrozenBetweenRequestsHost(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !string.IsNullOrWhiteSpace(configuration[AwsLambdaFunctionNameKey])
            || ControlPlaneLambdaConnectionRelease.IsEventHandlerProfile(configuration);
    }

    // Custom-code and native profiles never qualify as the import workload.
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
