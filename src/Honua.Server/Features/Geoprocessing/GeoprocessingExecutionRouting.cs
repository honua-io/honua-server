// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// How a catalog process is executed (or why it is not yet executable). This is the
/// single, authoritative source of truth for catalog honesty: the
/// catalog==executable conformance test cross-checks this routing against the
/// processes the <see cref="Execution.GeoprocessingDispatchJobExecutor"/> actually
/// routes, so the catalog can never silently advertise a managed-vector process
/// that has no executor.
///
/// Kept as a server-side classifier rather than a field on
/// <see cref="ProcessDefinition"/> so routing policy stays policy-owned and
/// <c>Honua.Core</c> remains transport-neutral, mirroring
/// <see cref="ProcessDestructiveClassifier"/>.
/// </summary>
internal enum GeoprocessingExecutionRouting
{
    /// <summary>
    /// Pure-managed (NetTopologySuite, GDAL-free) vector process that runs in the
    /// lean serving image and MUST have a registered executor. These are the
    /// processes the catalog==executable conformance test enforces.
    /// </summary>
    ManagedVectorExecutable,

    /// <summary>
    /// Raster / surface family routed to the native GDAL heavyweight worker
    /// (feat/gdal-heavy-worker). Advertised in the catalog as a capability but
    /// NOT executable in the lean baseline — and the conformance test asserts it
    /// is honestly absent from the managed dispatcher rather than silently
    /// advertised as runnable.
    /// </summary>
    RoutedToNativeWorker,

    /// <summary>
    /// A vector-data process whose layer-level aggregation / mutation semantics
    /// (group-by dissolve, spatial-join, density binning, clustering, destructive
    /// data-management, scalar format conversion) are not implemented as a managed
    /// executor in this baseline. Inventoried for migration review; intentionally
    /// not claimed as executable.
    /// </summary>
    NotYetExecutable
}

/// <summary>
/// Maps catalog process ids to their <see cref="GeoprocessingExecutionRouting"/>.
/// </summary>
internal static class GeoprocessingExecutionRoutingClassifier
{
    // Managed-vector processes that MUST have a working executor. This is the
    // honest "catalog == executable" vector set: every id here is also a key in
    // GeoprocessingDispatchJobExecutor, enforced by the conformance test.
    private static readonly FrozenSet<string> ManagedVectorExecutableIds =
        new HashSet<string>(StringComparer.Ordinal)
        {
            // Single-geometry (primitive) vector executors.
            "geometry.buffer",
            "geometry.clip",
            "geometry.intersect",
            "geometry.project",
            "geometry.area",
            "geometry.length",
            "geometry.centroid",
            "geometry.convex-hull",
            "geometry.union",
            "geometry.dissolve",
            "geometry.simplify",
            "geometry.snap",
            // Layer-scope (feature-collection) executors added by this stream.
            "geometry.make-valid",
            "geometry.difference",
            "generalization.simplify-layer",
            "conversion.feature-project",
        }.ToFrozenSet(StringComparer.Ordinal);

    public static GeoprocessingExecutionRouting Classify(ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (ManagedVectorExecutableIds.Contains(definition.ProcessId))
        {
            return GeoprocessingExecutionRouting.ManagedVectorExecutable;
        }

        // Raster + surface families (and raster-output conversions) require GDAL
        // and run on the native heavyweight worker — never in the lean baseline.
        if (definition.Category is "raster" or "surface")
        {
            return GeoprocessingExecutionRouting.RoutedToNativeWorker;
        }

        if (definition.Category == "conversion" &&
            definition.OutputArtifactKinds.Contains(ArtifactKind.Raster))
        {
            return GeoprocessingExecutionRouting.RoutedToNativeWorker;
        }

        // Everything else (analytics aggregation, generalization.dissolve,
        // data-management mutation, scalar geometry-format conversion) is a
        // vector-data process whose managed executor is not built in this
        // baseline. Inventoried, not advertised as executable.
        return GeoprocessingExecutionRouting.NotYetExecutable;
    }
}
