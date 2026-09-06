// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// Owns the exhaustive execution classification for the built-in process catalog.
/// Protocol adapters consume the stamped <see cref="ProcessDefinition"/> metadata;
/// migration-evidence tiers do not participate in runtime eligibility decisions.
/// </summary>
internal static class ProcessExecutionCapabilityCatalog
{
    private static readonly FrozenSet<string> JobProcessIds = IdSet(
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
        "analytics.spatial-join-managed",
        "analytics.cluster-managed",
        "analytics.buffer-aggregate-managed",
        "analytics.density-managed",
        "analytics.hotspot-managed",
        "analytics.buffer-aggregate",
        "conversion.feature-project",
        "generalization.dissolve",
        "generalization.simplify-layer",
        "analytics.spatial-join",
        "enrichment.enrich",
        "overlay.clip",
        "overlay.intersect",
        "overlay.union",
        "overlay.erase",
        "overlay.merge",
        "overlay.split",
        "data-management.append",
        "proximity.near",
        "proximity.near-table",
        "statistics.summarize",
        "statistics.frequency",
        "statistics.calculate",
        "transform.attribute-rename",
        "transform.attribute-cast",
        "transform.computed-field",
        "transform.attribute-filter",
        "transform.attribute-join",
        "transform.aggregate",
        "transform.pivot",
        "transform.unpivot",
        "transform.spatial-filter",
        "transform.clip",
        "transform.dedup",
        "transform.reproject",
        "import.dataset",
        "imagery.classify",
        "surface.slope",
        "surface.aspect",
        "surface.hillshade",
        "surface.rugosity-tri",
        "surface.rugosity-tpi",
        "surface.roughness",
        "raster.clip",
        "raster.reproject",
        "raster.statistics",
        "raster.histogram",
        "raster.zonal-statistics",
        "raster.resample",
        "raster.interpolate-idw",
        "raster.interpolate-kriging",
        "raster.mosaic",
        "raster.map-algebra",
        "raster.spectral-index",
        "raster.reclassify",
        "proximity.euclidean-distance",
        "proximity.euclidean-allocation",
        "surface.contour",
        "surface.viewshed",
        "conversion.polygonize",
        "conversion.rasterize",
        "conversion.raster-format",
        "conversion.raster-reproject",
        "pcloud.translate",
        "gdal.gdalwarp",
        "gdal.ogr2ogr");

    private static readonly FrozenSet<string> ProtocolOnlyProcessIds = IdSet(
        "analytics.cluster",
        "analytics.density",
        "data-management.copy-features",
        "data-management.delete-features",
        "data-management.calculate-field",
        "conversion.geometry-format");

    private static readonly FrozenSet<string> WorkflowOnlyProcessIds = IdSet(
        "source.geojson",
        "source.csv",
        "source.honua-layer",
        "source.esri-featureserver",
        "source.ogc-features",
        "source.wfs",
        "source.postgis",
        "source.ogr",
        "sink.geojson-file",
        "sink.quarantine",
        "sink.external-postgis",
        "sink.honua-layer");

    private static readonly FrozenSet<string> SyncJobProcessIds = IdSet(
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
        "geometry.snap");

    /// <summary>
    /// Processes advertised for discovery that cannot execute on ANY entry point, keyed by
    /// process id with the canonical operator-facing reason.
    ///
    /// <para>
    /// This set is EMPTY and must stay empty: the 2026-09-06 catalog entry-point ruling
    /// admits no third state between "implemented" and "not advertised" — an operation is
    /// either callable on a declared entry point or it is absent from the catalog. The
    /// machinery is retained because <see cref="Classify"/> is exhaustive and fail-closed,
    /// so a future addition that lands here is refused by the gate instead of quietly
    /// becoming an advertised dead end (#4409).
    /// </para>
    /// </summary>
    internal static readonly FrozenDictionary<string, string> UnavailableReasons =
        FrozenDictionary<string, string>.Empty;

    /// <summary>Stamps one raw built-in definition with its required canonical classification.</summary>
    public static ProcessDefinition Classify(ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var processId = definition.ProcessId;
        var membershipCount = Convert.ToInt32(JobProcessIds.Contains(processId))
            + Convert.ToInt32(ProtocolOnlyProcessIds.Contains(processId))
            + Convert.ToInt32(WorkflowOnlyProcessIds.Contains(processId))
            + Convert.ToInt32(UnavailableReasons.ContainsKey(processId));
        if (membershipCount != 1)
        {
            throw new InvalidOperationException(
                $"Built-in process '{processId}' must have exactly one execution classification; found {membershipCount}.");
        }

        var kind = JobProcessIds.Contains(processId)
            ? ProcessExecutionKind.Job
            : ProtocolOnlyProcessIds.Contains(processId)
                ? ProcessExecutionKind.ProtocolOnly
                : WorkflowOnlyProcessIds.Contains(processId)
                    ? ProcessExecutionKind.WorkflowOnly
                    : ProcessExecutionKind.Unavailable;

        var entryPoints = kind switch
        {
            // A job process is reachable through the shared job runtime AND as a workflow
            // DAG node: WorkflowPackageService compiles a process node into the same
            // analysis-plan step the job runtime dispatches.
            ProcessExecutionKind.Job => ProcessEntryPoints.Job | ProcessEntryPoints.Workflow,
            ProcessExecutionKind.ProtocolOnly => ProcessEntryPoints.Protocol,
            ProcessExecutionKind.WorkflowOnly => ProcessEntryPoints.Workflow,
            _ => ProcessEntryPoints.None
        };

        var modes = kind switch
        {
            ProcessExecutionKind.Job => ProcessExecutionModes.Async
                | (SyncJobProcessIds.Contains(processId) ? ProcessExecutionModes.Sync : ProcessExecutionModes.None),
            ProcessExecutionKind.ProtocolOnly => ProcessExecutionModes.Sync,
            ProcessExecutionKind.WorkflowOnly => ProcessExecutionModes.Async,
            _ => ProcessExecutionModes.None
        };

        var configurationDependency = processId == "imagery.classify"
            ? "Geoprocessing:ImageryInference"
            : definition.RuntimeProfile == RuntimeProfiles.Native
                ? "runtime-profile:native"
                : null;

        var reason = kind switch
        {
            ProcessExecutionKind.ProtocolOnly =>
                "Callable only through its owning synchronous protocol endpoint; no process-job executor is registered.",
            ProcessExecutionKind.WorkflowOnly =>
                "Composable only as a workflow DAG source or sink; direct process execution is not supported.",
            ProcessExecutionKind.Unavailable => UnavailableReasons[processId],
            ProcessExecutionKind.Job when processId == "imagery.classify" =>
                "Requires a configured imagery-inference provider and endpoint.",
            ProcessExecutionKind.Job when definition.RuntimeProfile == RuntimeProfiles.Native =>
                "Requires a native-worker deployment that serves the native runtime profile.",
            _ => null
        };

        return definition with
        {
            ExecutionKind = kind,
            SupportedExecutionModes = modes,
            SupportedEntryPoints = entryPoints,
            ConfigurationDependency = configurationDependency,
            ExecutionCapabilityReason = reason
        };
    }

    /// <summary>Whether the process may be submitted through OGC API Processes.</summary>
    public static bool IsOgcCallable(ProcessDefinition definition)
        => ProcessExecutionEligibility.IsJobCallable(definition);

    /// <summary>
    /// Whether the process is composable as a workflow DAG node. Job and workflow-only
    /// processes both compile into an analysis-plan step; protocol-only processes have no
    /// dispatcher executor and must not be offered to a graph author.
    /// </summary>
    public static bool IsWorkflowComposable(ProcessDefinition definition)
        => ProcessExecutionEligibility.Declares(definition, ProcessEntryPoints.Workflow);

    private static FrozenSet<string> IdSet(params string[] processIds)
        => processIds.ToFrozenSet(StringComparer.Ordinal);
}
