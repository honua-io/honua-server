// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

internal static class LayerComputationBudget
{
    // The same admission repeats the bounded work the limit exists to cap, so the caller resubmits a narrower job instead of retrying.
    internal static JobExecutionResult Refusal(string message)
        => JobExecutionResult.Failed(message) with { IsRetryable = false };

    internal static long CountVertices(IEnumerable<IFeature> features)
        => features.Sum(feature => (long)(feature.Geometry?.NumPoints ?? 0));

    internal static void EnsureTopologyWork(long leftVertices, long rightVertices, long maxWork)
    {
        // Division avoids overflow even for independently constructed executor inputs.
        if (leftVertices > 0 && rightVertices > maxWork / leftVertices)
        {
            throw new TransformInputException(
                $"managed topology for {leftVertices} by {rightVertices} vertices exceeds " +
                $"Geoprocessing:Executors:MaxTopologyWork={maxWork}; stopped before computation. " +
                "Narrow the selection or simplify the input, then resubmit. " +
                "Raise this limit only after qualifying the worker's memory and execution time.");
        }
    }
}
