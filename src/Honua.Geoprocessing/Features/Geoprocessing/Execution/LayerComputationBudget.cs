// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>Conservative admission for managed operations that cannot observe cancellation inside a topology call.</summary>
internal static class LayerComputationBudget
{
    /// <summary>
    /// A deterministic input, resource-limit or deadline refusal. Retrying the same job repeats the same
    /// admission and the same bounded work, which multiplies the worker time the limit exists to cap and
    /// delays the actionable outcome behind retry backoff (#4629). The caller resubmits a narrower job.
    /// </summary>
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
