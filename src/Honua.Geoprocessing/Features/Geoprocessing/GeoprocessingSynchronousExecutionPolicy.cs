// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// Classifies deterministic, locally bounded catalog processes that protocol
/// adapters may safely expose through a synchronous execution contract.
/// </summary>
internal static class GeoprocessingSynchronousExecutionPolicy
{
    private static readonly HashSet<string> SyncEligibleProcessIds = new(StringComparer.Ordinal)
    {
        "geometry.buffer",
        "geometry.simplify",
        "geometry.project",
        "geometry.make-valid",
        "geometry.union",
        "geometry.intersect",
        "geometry.clip",
        "geometry.difference",
        "geometry.area",
        "geometry.length",
        "geometry.centroid",
        "geometry.convex-hull",
        "geometry.dissolve",
        "geometry.snap",
        "conversion.geometry-format",
    };

    internal static bool IsSynchronous(ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return SyncEligibleProcessIds.Contains(definition.ProcessId);
    }
}
