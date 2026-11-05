// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Import;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

internal enum LayerPublishRunnerMode
{
    InProcess,

    DurableLocal,

    DurableRemote,

    Unavailable,
}

// A frozen host cannot drain a local queue after it has responded, so the publish requires a remote worker.
internal sealed class LayerPublishRunnerDecision
{
    internal const string RunnerDependency = "layer-publish-runner";

    internal LayerPublishRunnerDecision(LayerPublishRunnerMode mode, ExecutionJobDefinition? workload = null)
    {
        if (mode == LayerPublishRunnerMode.DurableRemote)
        {
            ArgumentNullException.ThrowIfNull(workload);
        }

        Mode = mode;
        Workload = workload;
    }

    internal LayerPublishRunnerMode Mode { get; }

    internal ExecutionJobDefinition? Workload { get; }

    internal static LayerPublishRunnerMode Decide(bool durableRuntimeAvailable, bool frozenBetweenRequests)
    {
        if (frozenBetweenRequests)
        {
            return LayerPublishRunnerMode.Unavailable;
        }

        return durableRuntimeAvailable
            ? LayerPublishRunnerMode.DurableLocal
            : LayerPublishRunnerMode.InProcess;
    }

    internal static ExecutionJobDefinition? SelectRemoteWorkload(
        IEnumerable<ExecutionJobDefinition> definitions,
        IEnumerable<IBatchComputeBackend> backends)
    {
        var registered = backends.ToArray();
        var candidates = definitions.Where(definition =>
            !string.Equals(definition.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal)
            && registered.Resolve(definition.Backend, definition.TargetKind) is not null
            && RuntimeProfiles.CanClaim(RuntimeProfiles.DefaultAccepted, definition.RuntimeProfile)).ToArray();
        return candidates.FirstOrDefault(static definition => definition.Kind == ExecutionJobKind.LayerPublish)
            ?? candidates.FirstOrDefault(static definition => definition.Kind == ExecutionJobKind.Geoprocessing);
    }

    internal static LayerPublishRunnerDecision Resolve(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var substrate = services.GetService<IOptions<DurableJobSubstrateOptions>>()?.Value ?? new DurableJobSubstrateOptions();
        var durable = substrate.Classify(
            services.GetService<IExecutionJobStore>() is not null,
            services.GetService<IJobQueue>() is not null) == DurableJobSubstrateCause.Available;
        var controlPlane = services.GetService<IOptionsMonitor<ControlPlaneOptions>>()?.CurrentValue;
        var workload = durable && controlPlane is not null
            ? SelectRemoteWorkload(
                ConfigurationExecutionJobDefinitionRegistry.BuildDefinitions(controlPlane),
                services.GetServices<IBatchComputeBackend>())
            : null;
        if (workload is not null)
        {
            return new LayerPublishRunnerDecision(LayerPublishRunnerMode.DurableRemote, workload);
        }

        var configuration = services.GetService<IConfiguration>();
        var frozen = configuration is not null && BackgroundImportRunnerDecision.IsFrozenBetweenRequestsHost(configuration);
        return new LayerPublishRunnerDecision(Decide(durable, frozen));
    }
}
