// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.GeoETL.Services.Connectors;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Geoprocessing.Execution;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Catalog honesty conformance: every VECTOR process the built-in catalog
/// advertises as managed-executable must have a working executor registered in
/// the geoprocessing dispatcher, and every raster/surface process must be
/// honestly marked routed-to-native-worker rather than silently advertised as
/// runnable. This is the test that closes the historical "12 of 45 advertised"
/// gap for the vector families: it FAILS if a managed-vector process is
/// advertised but not executable, or if a routed-to-native process leaks into
/// the managed dispatcher.
/// </summary>
public sealed class CatalogExecutableConformanceTests
{
    private readonly BuiltInProcessCatalog _catalog = new();

    [UnitTest]
    public void EveryManagedVectorProcess_HasAWorkingExecutor()
    {
        var executable = DispatcherSupportedProcessIds();

        var advertisedVector = _catalog.ListProcesses()
            .Where(p => GeoprocessingExecutionRoutingClassifier.Classify(p)
                == GeoprocessingExecutionRouting.ManagedVectorExecutable)
            .Select(p => p.ProcessId)
            .ToList();

        advertisedVector.Should().NotBeEmpty();

        foreach (var processId in advertisedVector)
        {
            executable.Should().Contain(
                processId,
                $"catalog advertises managed-vector process '{processId}' which therefore MUST have a working executor");
        }
    }

    [UnitTest]
    public void EveryDispatcherExecutor_IsAnAdvertisedManagedVectorProcess()
    {
        var executable = DispatcherSupportedProcessIds();

        foreach (var processId in executable)
        {
            var definition = _catalog.GetProcess(processId);
            definition.Should().NotBeNull(
                $"dispatcher routes '{processId}' so it must be advertised in the catalog");
            GeoprocessingExecutionRoutingClassifier.Classify(definition!)
                .Should().Be(
                    GeoprocessingExecutionRouting.ManagedVectorExecutable,
                    $"every executable process '{processId}' must be classified managed-vector-executable");
        }
    }

    [UnitTest]
    public void RasterAndSurfaceProcesses_AreRoutedToNativeWorker_AndAbsentFromManagedDispatcher()
    {
        var executable = DispatcherSupportedProcessIds();

        var rasterSurface = _catalog.ListProcesses()
            .Where(p => p.Category is "raster" or "surface")
            .ToList();

        rasterSurface.Should().NotBeEmpty();

        foreach (var definition in rasterSurface)
        {
            GeoprocessingExecutionRoutingClassifier.Classify(definition)
                .Should().Be(
                    GeoprocessingExecutionRouting.RoutedToNativeWorker,
                    $"raster/surface process '{definition.ProcessId}' must be routed to the native GDAL worker");
            executable.Should().NotContain(
                definition.ProcessId,
                $"raster/surface process '{definition.ProcessId}' must NOT be executable in the lean managed baseline");
        }
    }

    [UnitTest]
    public void EveryCatalogProcess_HasExactlyOneRouting()
    {
        // Sanity: the classifier partitions the whole catalog (no process is left
        // unclassified), so honesty claims cover every advertised process.
        foreach (var definition in _catalog.ListProcesses())
        {
            var routing = GeoprocessingExecutionRoutingClassifier.Classify(definition);
            routing.Should().BeOneOf(
                GeoprocessingExecutionRouting.ManagedVectorExecutable,
                GeoprocessingExecutionRouting.RoutedToNativeWorker,
                GeoprocessingExecutionRouting.NotYetExecutable);
        }
    }

    [UnitTest]
    public void ManagedVectorExecutableCount_CoversTheVectorProcesses()
    {
        // Documents the closed honesty gap: 18 vector processes now execute
        // (12 single-geometry primitives + 6 layer-scope), up from the
        // 12 single-geometry executors before this stream. The two group-aware
        // additions are generalization.dissolve and analytics.spatial-join
        // (aggregating one-to-one summarizing form).
        var managedVector = _catalog.ListProcesses()
            .Count(p => GeoprocessingExecutionRoutingClassifier.Classify(p)
                == GeoprocessingExecutionRouting.ManagedVectorExecutable);

        managedVector.Should().Be(18);
    }

    private static IReadOnlyCollection<string> DispatcherSupportedProcessIds()
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
            new LayerGeometryJobExecutor(
                [new InlineGeoJsonLayerFeatureSource()],
                monitor,
                NullLogger<LayerGeometryJobExecutor>.Instance),
            NullLogger<GeoprocessingDispatchJobExecutor>.Instance);

        return dispatcher.SupportedProcessIds;
    }
}
