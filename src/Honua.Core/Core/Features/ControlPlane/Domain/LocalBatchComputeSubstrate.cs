// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

public enum BatchComputeSubstrateProfile
{
    SingleHost = 0,

    MultiNode = 1,

    Serverless = 2,
}

public static class LocalBatchComputeSubstrate
{
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

public readonly record struct LocalBatchComputeCompatibility(bool IsCompatible, string? Reason)
{
    public static LocalBatchComputeCompatibility Compatible { get; } = new(true, null);
}
