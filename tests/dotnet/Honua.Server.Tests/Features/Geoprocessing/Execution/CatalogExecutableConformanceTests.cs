// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Catalog-honesty conformance, rewritten against the REALITY of #1185 trunk
/// (this replaces the earlier worldview that assumed an 18-id managed-vector set
/// and a GeoprocessingExecutionRoutingClassifier that does not hold on trunk).
///
/// The premise: the catalog advertises processes that reach execution through
/// THREE different surfaces, only one of which is the geoprocessing job
/// dispatcher. A process is dishonest only if it claims job-executability and
/// has no executor. So this test classifies every advertised process into one of:
///
///   - JOB-EXECUTABLE — runs through GeoprocessingDispatchJobExecutor. Every one
///     of these MUST have an entry in the dispatcher handler map, and the test
///     FAILS if any is missing. (The historical "catalog advertises it but no
///     executor exists" gap, e.g. geometry.make-valid / geometry.difference, is
///     exactly what this guards.)
///   - PROTOCOL-ONLY — runs synchronously through the layer-scoped PostGIS
///     SpatialAnalytics protocol or other non-job surfaces, NOT the dispatcher
///     (analytics.cluster/spatial-join/buffer-aggregate/density, generalization.*,
///     data-management.*, conversion.feature-project). These are NOT required to
///     be job-executable and MUST be absent from the dispatcher.
///   - ROUTED-TO-NATIVE — raster/surface (and the raster conversion idioms) belong
///     to the GDAL native worker (a later stream). NOT required to be
///     job-executable in the GDAL-free serving image and MUST be absent here.
///
/// The classification below is the source of truth. It is exhaustive: every
/// advertised process must be listed in exactly one bucket, so a newly added
/// catalog entry that nobody classified fails the partition check loudly.
/// </summary>
public sealed class CatalogExecutableConformanceTests
{
    private readonly BuiltInProcessCatalog _catalog = new();

    // Processes that run through the geoprocessing JOB DISPATCHER. Each MUST have
    // a handler in GeoprocessingDispatchJobExecutor (the honesty contract).
    private static readonly string[] JobExecutableProcessIds =
    {
        // Deterministic single-geometry vector primitives (managed NTS).
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
        // Managed spatial-join (distinct from the PostGIS-protocol analytics.spatial-join).
        "analytics.spatial-join-managed",
        // Managed analytics counterparts for cluster / buffer-aggregate / density (#1260).
        // The unsuffixed ids stay in the protocol-only bucket; these -managed ids are
        // their workflow-reachable, FeatureCollection-in/out, no-Postgres counterparts.
        "analytics.cluster-managed",
        "analytics.buffer-aggregate-managed",
        "analytics.density-managed",
        // Spatial-statistics tool pack (#2142): Hot Spot Analysis (Getis-Ord Gi*).
        "analytics.hotspot-managed",
        // Layer-aware, layer-SOURCED managed ops (#2322, #2325): the job-executable
        // counterparts of the layer-scoped analytics/generalization/conversion ops.
        // Each streams a Honua catalog layer through source.honua-layer and runs the
        // managed op in one dispatched job, so the per-op OGC API - Processes
        // projections (#1382) that advertise a layerId input reach a terminal state.
        "analytics.buffer-aggregate",
        "conversion.feature-project",
        "generalization.dissolve",
        "generalization.simplify-layer",
        // Two-layer analytics.spatial-join (#2322): resolves both the target layerId and
        // the joinLayerId through source.honua-layer and joins them in one dispatched job.
        "analytics.spatial-join",
        // Layer-aware overlay tool pack (#2206, #2139): managed NTS, two
        // FeatureCollections in, one FeatureCollection/table out.
        "overlay.clip",
        "overlay.intersect",
        "overlay.union",
        "overlay.erase",
        "overlay.merge",
        "overlay.split",
        "data-management.append",
        // Proximity tool pack (#2139).
        "proximity.near",
        "proximity.near-table",
        // Statistics/summarization tool pack (#2140): table-producing aggregates.
        "statistics.summarize",
        "statistics.frequency",
        "statistics.calculate",
        // GeoETL transforms (managed NTS, FeatureCollection in/out).
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
        // GeoETL sources.
        "source.geojson",
        "source.csv",
        // First-class remote DAG source connectors: each runs through the dispatcher
        // via a per-process RemoteSourceExecutor that reuses an existing import reader.
        "source.honua-layer",
        "source.esri-featureserver",
        "source.ogc-features",
        "source.wfs",
        "source.postgis",
        // GeoETL sinks.
        "sink.geojson-file",
        "sink.quarantine",
        "sink.external-postgis",
        // Managed honua-layer sink: loads a FeatureCollection into a named catalog
        // layer via the optional IHonuaLayerSink capability. The executor is always
        // registered (the capability is optional), so it is dispatcher-routable.
        "sink.honua-layer",
        // Durable import pipeline (#1630): managed orchestration job that composes
        // the import / publishing / raster services through an IServiceScopeFactory.
        "import.dataset",
        // Imagery/ML delegated inference (#2241): the managed dispatcher executes
        // the delegation itself (an HTTP exchange with the configured cloud
        // backend); an unconfigured deployment fails the job with a clear
        // unavailability message rather than stubbing a result.
        "imagery.classify",
    };

    // Processes that execute ONLY through the synchronous PostGIS SpatialAnalytics
    // protocol or another non-dispatcher surface (layer-scoped, Pro-gated, or
    // destructive edit paths). NOT job-dispatchable; MUST be absent from the
    // dispatcher. analytics.spatial-join is NO LONGER here: #2322 added a layer-aware
    // job executor (LayerSpatialJoinExecutor) that resolves both the target and join
    // layers through source.honua-layer, so it is now job-executable above.
    private static readonly string[] ProtocolOnlyProcessIds =
    {
        "analytics.cluster",
        "analytics.density",
        "data-management.copy-features",
        "data-management.delete-features",
        "data-management.calculate-field",
        "conversion.geometry-format",
    };

    // Processes routed to the GDAL native worker. NOT executable in the GDAL-free
    // serving image; MUST be absent from the managed dispatcher. The gdal.* family
    // are the processes the heavyweight worker's executors actually handle
    // (GdalRasterReprojectJobExecutor / GdalVectorConvertJobExecutor) and are the
    // only catalog entries that declare RuntimeProfile = native; the raster.* /
    // surface.* / conversion.raster-* idioms are catalog-advertised native-routed
    // operations whose canonical execution likewise belongs to the worker, not the
    // lean dispatcher.
    private static readonly string[] RoutedToNativeProcessIds =
    {
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
        // Raster analysis tool pack (#2141): resample / IDW interpolation / mosaic
        // run out-of-process in the GDAL worker; kriging is advertised but flagged
        // unsupported (the worker FAILS it with a clear message). All declare the
        // native runtime profile and have NO lean-dispatcher executor.
        "raster.resample",
        "raster.interpolate-idw",
        "raster.interpolate-kriging",
        "raster.mosaic",
        // Raster analysis & terrain GP tool pack (#2239 / #2240): all run
        // out-of-process in the GDAL worker (gdal_calc.py / gdal_proximity.py /
        // gdal_contour / gdal_viewshed / gdal_polygonize.py / gdal_rasterize).
        // proximity.euclidean-allocation runs the custom gdal_euclidean_allocation.py
        // worker step (#2255). All declare the native runtime profile and have NO
        // lean-dispatcher executor.
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
        // Point-cloud conversion (#1854): LAZ/COPC decompression + optional
        // reprojection executed out-of-process by the heavyweight GDAL/PDAL worker
        // (PdalPointCloudConvertJobExecutor) via `pdal translate`. Declares
        // RuntimeProfile = native and has NO executor in the lean dispatcher.
        "pcloud.translate",
        // Native GDAL worker processes (executable in the out-of-process worker,
        // NOT in the lean dispatcher handler map).
        "gdal.gdalwarp",
        "gdal.ogr2ogr",
        // GDAL/OGR-backed import reader (GdalVectorSourceReadJobExecutor): the
        // native counterpart to the managed source.geojson / source.csv readers,
        // canonicalizing the broader OGR format universe (FileGDB, GML, KML, TAB,
        // Shapefile, GeoPackage, FlatGeobuf) into the standard FeatureCollection
        // artifact. Declares RuntimeProfile = native; no lean-dispatcher executor.
        "source.ogr",
    };

    [UnitTest]
    public void Classification_PartitionsEveryAdvertisedProcess_ExactlyOnce()
    {
        // Honesty depends on covering EVERY advertised process. If a new catalog
        // entry is not classified into one of the three buckets, this fails so
        // the author must declare which surface executes it.
        var advertised = _catalog.ListProcesses().Select(p => p.ProcessId).ToHashSet(StringComparer.Ordinal);
        var classified = JobExecutableProcessIds
            .Concat(ProtocolOnlyProcessIds)
            .Concat(RoutedToNativeProcessIds)
            .ToList();

        classified.Should().OnlyHaveUniqueItems("no process may be claimed by two execution surfaces");
        classified.Should().BeEquivalentTo(
            advertised,
            "every advertised process must be classified into exactly one execution surface, and every classified id must be advertised");
    }

    [UnitTest]
    public void EveryJobExecutableProcess_HasADispatcherExecutor()
    {
        var executable = DispatcherSupportedProcessIds();

        foreach (var processId in JobExecutableProcessIds)
        {
            _catalog.GetProcess(processId).Should().NotBeNull(
                $"job-executable process '{processId}' must be advertised in the catalog");
            executable.Should().Contain(
                processId,
                $"catalog advertises '{processId}' as job-executable, so it MUST have an executor in the dispatcher handler map");
        }
    }

    [UnitTest]
    public void EveryDispatcherExecutor_IsAClassifiedJobExecutableProcess()
    {
        // No orphan executors: everything the dispatcher routes must be an
        // advertised, job-executable-classified process.
        var executable = DispatcherSupportedProcessIds();

        foreach (var processId in executable)
        {
            _catalog.GetProcess(processId).Should().NotBeNull(
                $"dispatcher routes '{processId}', so it must be advertised in the catalog");
            JobExecutableProcessIds.Should().Contain(
                processId,
                $"dispatcher routes '{processId}', so it must be classified job-executable");
        }
    }

    [UnitTest]
    public void ProtocolOnlyAndNativeProcesses_AreAbsentFromTheManagedDispatcher()
    {
        var executable = DispatcherSupportedProcessIds();

        foreach (var processId in ProtocolOnlyProcessIds)
        {
            executable.Should().NotContain(
                processId,
                $"'{processId}' runs through the PostGIS SpatialAnalytics protocol (or another non-job surface), not the job dispatcher");
        }

        foreach (var processId in RoutedToNativeProcessIds)
        {
            executable.Should().NotContain(
                processId,
                $"'{processId}' is routed to the GDAL native worker and must not be executable in the GDAL-free managed baseline");
        }
    }

    [UnitTest]
    public void GeometryFamily_IsFullyJobExecutable_AndMatchesTheSyncExecutionPolicy()
    {
        // The geometry.* family is the canonical job-executable vector set: every
        // geometry.* the catalog advertises must have an executor. This is the
        // regression guard for the make-valid / difference gap specifically — both
        // were advertised + flagged synchronous on trunk but had no executor.
        var executable = DispatcherSupportedProcessIds();

        var geometryIds = _catalog.ListProcesses()
            .Where(p => p.Category == "geometry")
            .Select(p => p.ProcessId)
            .ToList();

        geometryIds.Should().NotBeEmpty();
        foreach (var processId in geometryIds)
        {
            executable.Should().Contain(
                processId,
                $"every advertised geometry.* process must be job-executable; '{processId}' is missing an executor");
        }
    }

    [UnitTest]
    public void NativeRoutedGdalProcesses_DeclareTheNativeRuntimeProfile_AndAreAbsentFromTheManagedDispatcher()
    {
        // The data-driven native-profile contract: every gdal.* / surface.* /
        // raster.* process declares RuntimeProfile = native so the submit path
        // stamps the spec native and routes the job to the out-of-process GDAL
        // worker. The worker's own test project asserts the worker dispatcher
        // routes these ids; here we lock in the catalog declaration AND that
        // the lean dispatcher has no executor for them, so the routing decision
        // and the GDAL-free baseline agree.
        // Derive the native-profile assertion set from the classification source of
        // truth so a newly added native-routed id (the #2141 / #2239 / #2240 raster &
        // terrain GP packs) is covered automatically instead of drifting away from a
        // hand-maintained list.
        var nativeExecutableProcessIds = RoutedToNativeProcessIds;
        var managedExecutable = DispatcherSupportedProcessIds();

        foreach (var processId in nativeExecutableProcessIds)
        {
            var definition = _catalog.GetProcess(processId);
            definition.Should().NotBeNull($"the catalog must advertise native process '{processId}'");
            definition!.RuntimeProfile.Should().Be(
                RuntimeProfiles.Native,
                $"'{processId}' executes out-of-process in the GDAL worker, so it must declare the native runtime profile for the submit path to stamp the spec");
            RoutedToNativeProcessIds.Should().Contain(processId);
            managedExecutable.Should().NotContain(
                processId,
                $"'{processId}' is native-routed and must NOT have an executor in the GDAL-free managed dispatcher");
        }
    }

    [UnitTest]
    public void EveryManagedClassifiedProcess_DeclaresTheManagedRuntimeProfile()
    {
        // Symmetry guard: nothing outside the native-routed bucket may claim the
        // native profile, so a misclassified native declaration cannot silently
        // strand a job on the wrong worker.
        var nativeRouted = RoutedToNativeProcessIds.ToHashSet(StringComparer.Ordinal);

        foreach (var process in _catalog.ListProcesses())
        {
            if (nativeRouted.Contains(process.ProcessId))
            {
                continue;
            }

            RuntimeProfiles.Normalize(process.RuntimeProfile).Should().Be(
                RuntimeProfiles.Managed,
                $"'{process.ProcessId}' is not routed to the native worker, so it must run under the managed/default profile");
        }
    }

    private IReadOnlyCollection<string> DispatcherSupportedProcessIds()
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = 50L * 1024L * 1024L,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();

        IProcessExecutor[] executors =
        {
            new GeometryBufferJobExecutor(monitor, NullLogger<GeometryBufferJobExecutor>.Instance),
            new GeometryClipJobExecutor(monitor, NullLogger<GeometryClipJobExecutor>.Instance),
            new GeometryIntersectJobExecutor(monitor, NullLogger<GeometryIntersectJobExecutor>.Instance),
            new GeometryProjectJobExecutor(monitor, NullLogger<GeometryProjectJobExecutor>.Instance),
            new GeometryAreaJobExecutor(monitor, NullLogger<GeometryAreaJobExecutor>.Instance),
            new GeometryUnionJobExecutor(monitor, NullLogger<GeometryUnionJobExecutor>.Instance),
            new GeometryCentroidJobExecutor(monitor, NullLogger<GeometryCentroidJobExecutor>.Instance),
            new GeometryLengthJobExecutor(monitor, NullLogger<GeometryLengthJobExecutor>.Instance),
            new GeometryConvexHullJobExecutor(monitor, NullLogger<GeometryConvexHullJobExecutor>.Instance),
            new GeometryDissolveJobExecutor(monitor, NullLogger<GeometryDissolveJobExecutor>.Instance),
            new GeometrySimplifyJobExecutor(monitor, NullLogger<GeometrySimplifyJobExecutor>.Instance),
            new GeometrySnapJobExecutor(monitor, NullLogger<GeometrySnapJobExecutor>.Instance),
            new GeometryMakeValidJobExecutor(monitor, NullLogger<GeometryMakeValidJobExecutor>.Instance),
            new GeometryDifferenceJobExecutor(monitor, NullLogger<GeometryDifferenceJobExecutor>.Instance),
            new ManagedSpatialJoinExecutor(monitor),
            new ManagedClusterExecutor(monitor),
            new ManagedBufferAggregateExecutor(monitor),
            new ManagedDensityExecutor(monitor),
            new ManagedHotSpotExecutor(monitor),
            new LayerBufferAggregateExecutor(scopeFactory, monitor, NullLogger<LayerBufferAggregateExecutor>.Instance),
            new LayerFeatureProjectExecutor(scopeFactory, monitor, NullLogger<LayerFeatureProjectExecutor>.Instance),
            new LayerDissolveExecutor(scopeFactory, monitor, NullLogger<LayerDissolveExecutor>.Instance),
            new LayerSimplifyExecutor(scopeFactory, monitor, NullLogger<LayerSimplifyExecutor>.Instance),
            new LayerSpatialJoinExecutor(scopeFactory, monitor, NullLogger<LayerSpatialJoinExecutor>.Instance),
            new OverlayClipExecutor(monitor),
            new OverlayIntersectExecutor(monitor),
            new OverlayUnionExecutor(monitor),
            new OverlayEraseExecutor(monitor),
            new OverlayMergeExecutor(monitor),
            new OverlaySplitExecutor(monitor),
            new DataManagementAppendExecutor(monitor),
            new ProximityNearExecutor(monitor),
            new ProximityNearTableExecutor(monitor),
            new StatisticsSummarizeExecutor(monitor),
            new StatisticsFrequencyExecutor(monitor),
            new StatisticsCalculateExecutor(monitor),
            new AttributeRenameTransformExecutor(monitor),
            new AttributeCastTransformExecutor(monitor),
            new ComputedFieldTransformExecutor(monitor),
            new AttributeFilterTransformExecutor(monitor),
            new AttributeJoinTransformExecutor(monitor),
            new AggregateTransformExecutor(monitor),
            new PivotTransformExecutor(monitor),
            new UnpivotTransformExecutor(monitor),
            new SpatialFilterTransformExecutor(monitor),
            new ClipTransformExecutor(monitor),
            new DedupTransformExecutor(monitor),
            new ReprojectTransformExecutor(monitor),
            new GeoJsonSourceExecutor(monitor),
            new CsvSourceExecutor(monitor),
            new GeoJsonFileSinkExecutor(monitor),
            new QuarantineSinkExecutor(monitor),
            new ExternalPostgisSinkExecutor(monitor),
            new HonuaLayerSinkExecutor(monitor, NullLogger<HonuaLayerSinkExecutor>.Instance),
            new ImportDatasetJobExecutor(
                Substitute.For<IServiceScopeFactory>(),
                NullLogger<ImportDatasetJobExecutor>.Instance,
                Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>()),
            new ImageryInferenceJobExecutor(
                Substitute.For<IOptionsMonitor<Honua.Geoprocessing.Inference.ImageryInferenceOptions>>(),
                monitor,
                [],
                NullLogger<ImageryInferenceJobExecutor>.Instance),
        };

        // Remote DAG source connectors self-register as IProcessExecutor, so they flow
        // through the same single route-table scan as every other per-process executor.
        var allExecutors = executors.Concat(BuildRemoteSourceExecutors(monitor)).ToArray();

        var dispatcher = new GeoprocessingDispatchJobExecutor(
            allExecutors,
            NullLogger<GeoprocessingDispatchJobExecutor>.Instance);

        return dispatcher.SupportedProcessIds;
    }

    private static RemoteSourceExecutor[] BuildRemoteSourceExecutors(
        IOptionsMonitor<GeoprocessingExecutorOptions> monitor)
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        string[] sourceIds =
        [
            "source.honua-layer",
            "source.esri-featureserver",
            "source.ogc-features",
            "source.wfs",
            "source.postgis",
        ];

        return sourceIds
            .Select(id => RemoteSourceExecutor.ForProcess(
                id,
                scopeFactory,
                monitor,
                NullLogger<RemoteSourceExecutor>.Instance))
            .ToArray();
    }
}
