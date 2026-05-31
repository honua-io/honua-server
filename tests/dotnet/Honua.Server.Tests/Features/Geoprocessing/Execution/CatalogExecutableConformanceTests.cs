// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;
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
        // GeoETL transforms (managed NTS, FeatureCollection in/out).
        "transform.attribute-rename",
        "transform.attribute-cast",
        "transform.computed-field",
        "transform.attribute-filter",
        "transform.spatial-filter",
        "transform.clip",
        "transform.dedup",
        "transform.reproject",
        // GeoETL sources.
        "source.geojson",
        "source.csv",
        // GeoETL sinks.
        "sink.geojson-file",
        "sink.quarantine",
        "sink.external-postgis",
    };

    // Processes that execute ONLY through the synchronous PostGIS SpatialAnalytics
    // protocol or another non-dispatcher surface (layer-scoped, Pro-gated, or
    // destructive edit paths). NOT job-dispatchable; MUST be absent from the
    // dispatcher. analytics.spatial-join lives here — its job-executable managed
    // counterpart is analytics.spatial-join-managed above.
    private static readonly string[] ProtocolOnlyProcessIds =
    {
        "analytics.cluster",
        "analytics.spatial-join",
        "analytics.buffer-aggregate",
        "analytics.density",
        "generalization.simplify-layer",
        "generalization.dissolve",
        "data-management.copy-features",
        "data-management.delete-features",
        "data-management.calculate-field",
        "conversion.geometry-format",
        "conversion.feature-project",
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
        "conversion.raster-format",
        "conversion.raster-reproject",
        // Native GDAL worker processes (executable in the out-of-process worker,
        // NOT in the lean dispatcher handler map).
        "gdal.gdalwarp",
        "gdal.ogr2ogr",
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
        var nativeExecutableProcessIds = new[]
        {
            "gdal.gdalwarp",
            "gdal.ogr2ogr",
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
        };
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

        var dispatcher = new GeoprocessingDispatchJobExecutor(
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
            new AttributeRenameTransformExecutor(monitor),
            new AttributeCastTransformExecutor(monitor),
            new ComputedFieldTransformExecutor(monitor),
            new AttributeFilterTransformExecutor(monitor),
            new SpatialFilterTransformExecutor(monitor),
            new ClipTransformExecutor(monitor),
            new DedupTransformExecutor(monitor),
            new ReprojectTransformExecutor(monitor),
            new GeoJsonSourceExecutor(monitor),
            new CsvSourceExecutor(monitor),
            new GeoJsonFileSinkExecutor(monitor),
            new QuarantineSinkExecutor(monitor),
            new ExternalPostgisSinkExecutor(monitor),
            NullLogger<GeoprocessingDispatchJobExecutor>.Instance);

        return dispatcher.SupportedProcessIds;
    }
}
