// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Server.Features.Import;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>Where this host runs a layer publish that exceeded the row threshold (honua-server#5948).</summary>
internal enum LayerPublishRunnerMode
{
    /// <summary>Long-lived host without a durable job substrate. The publish runs on a background task.</summary>
    InProcess,

    /// <summary>Redis-backed execution job drained by <c>JobExecutionService</c> on this host.</summary>
    DurableLocal,

    /// <summary>A host frozen between requests. A large publish is refused.</summary>
    Unavailable,
}

/// <summary>
/// Same substrate facts as background import, without a remote batch backend: a frozen host
/// cannot drain a local queue after it responds, even when Redis is present.
/// </summary>
internal sealed class LayerPublishRunnerDecision
{
    /// <summary>Missing-dependency id on the typed 503. Redis may be present; the runner is not.</summary>
    internal const string RunnerDependency = "layer-publish-runner";

    internal LayerPublishRunnerDecision(LayerPublishRunnerMode mode) => Mode = mode;

    internal LayerPublishRunnerMode Mode { get; }

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

    internal static LayerPublishRunnerDecision Resolve(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var substrate = services.GetService<IOptions<DurableJobSubstrateOptions>>()?.Value ?? new DurableJobSubstrateOptions();
        var durable = substrate.Classify(
            services.GetService<IExecutionJobStore>() is not null,
            services.GetService<IJobQueue>() is not null) == DurableJobSubstrateCause.Available;
        var configuration = services.GetService<IConfiguration>();
        var frozen = configuration is not null && BackgroundImportRunnerDecision.IsFrozenBetweenRequestsHost(configuration);
        return new LayerPublishRunnerDecision(Decide(durable, frozen));
    }
}
