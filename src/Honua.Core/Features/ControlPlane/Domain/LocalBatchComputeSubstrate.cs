// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>
/// The deployment substrate profile that determines whether the single-host local batch-compute
/// backends (the in-process <c>local</c> backend and the child-process <c>honua-local-process</c>
/// pool) can operate. Their job state lives in an in-process registry that cannot survive a host
/// restart or be observed from another node, so they are only safe on a single host with durable
/// local storage.
/// </summary>
public enum BatchComputeSubstrateProfile
{
    /// <summary>
    /// A single long-lived host with durable local storage (the on-prem/air-gapped default). The
    /// local backends are fully supported.
    /// </summary>
    SingleHost = 0,

    /// <summary>
    /// Multiple replicas behind a load balancer. A job launched on one node cannot be observed from
    /// another, so the local backends are unusable unless a shared work directory makes their launch
    /// state reconstructable across nodes.
    /// </summary>
    MultiNode = 1,

    /// <summary>
    /// A serverless/ephemeral runtime (AWS Lambda, Azure Functions, Cloud Run) whose filesystem and
    /// process are frozen/torn down between invocations. The local backends can never work here.
    /// </summary>
    Serverless = 2,
}

/// <summary>
/// Pure decision logic for whether the single-host local batch-compute backends can operate on a
/// given deployment substrate. Kept side-effect-free (no environment/config reads) so it is trivially
/// unit-testable; callers resolve the effective profile and shared-work-dir signal and pass them in.
/// </summary>
/// <remarks>
/// This is the fail-closed complement to the deploy store, which refuses an operation on an
/// incompatible host rather than silently accepting it. On an incompatible substrate the local
/// backend otherwise observes every job as "process lost" from a node that never launched it and the
/// reconciler re-queues it, churning with no operator signal.
/// </remarks>
public static class LocalBatchComputeSubstrate
{
    /// <summary>
    /// Evaluates whether the local batch-compute backends can operate on the substrate.
    /// </summary>
    /// <param name="profile">The effective deployment substrate profile.</param>
    /// <param name="hasSharedWorkDir">
    /// Whether the operator has asserted a shared/persistent work directory reachable from every node
    /// (only relevant to <see cref="BatchComputeSubstrateProfile.MultiNode"/>).
    /// </param>
    /// <returns>The compatibility decision, including an operator-facing reason when incompatible.</returns>
    public static LocalBatchComputeCompatibility Evaluate(BatchComputeSubstrateProfile profile, bool hasSharedWorkDir)
        => profile switch
        {
            BatchComputeSubstrateProfile.Serverless => new LocalBatchComputeCompatibility(
                false,
                "The local batch-compute backends require a long-lived host with durable local storage, "
                + "but this deployment is a serverless/ephemeral substrate whose process and filesystem are "
                + "frozen or torn down between invocations. Target a remote batch backend "
                + "(honua-aws-batch, Azure Batch, or a Kubernetes Job) instead."),
            BatchComputeSubstrateProfile.MultiNode when !hasSharedWorkDir => new LocalBatchComputeCompatibility(
                false,
                "The local batch-compute backends track launched jobs in an in-process registry that a "
                + "sibling replica cannot observe, so on a multi-node deployment without a shared work "
                + "directory a job launched on one node is seen as lost by another and re-queued forever. "
                + "Configure a shared work directory (ControlPlane:Substrate:SharedWorkDir=true) or target a "
                + "remote batch backend."),
            _ => LocalBatchComputeCompatibility.Compatible,
        };
}

/// <summary>
/// The result of a <see cref="LocalBatchComputeSubstrate.Evaluate"/> decision.
/// </summary>
/// <param name="IsCompatible">Whether the local batch backends can operate on the substrate.</param>
/// <param name="Reason">An operator-facing explanation when incompatible; otherwise null.</param>
public readonly record struct LocalBatchComputeCompatibility(bool IsCompatible, string? Reason)
{
    /// <summary>A compatible decision with no reason.</summary>
    public static LocalBatchComputeCompatibility Compatible { get; } = new(true, null);
}
