// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class GeoprocessingSecurityAndSpatialParityTests
{
    [UnitTest]
    public async Task ImportDataset_TenantPartitionAndSymlinkRejection()
    {
        var stagingRoot = Path.Join(Path.GetTempPath(), $"honua-import-sec-{Guid.NewGuid():N}");
        var tenantADir = Path.Join(stagingRoot, "tenant-a");
        var tenantBDir = Path.Join(stagingRoot, "tenant-b");
        Directory.CreateDirectory(tenantADir);
        Directory.CreateDirectory(tenantBDir);

        var tenantBFile = Path.Join(tenantBDir, "secret.geojson");
        await File.WriteAllTextAsync(tenantBFile, "{}");

        var outsideTarget = Path.Join(Path.GetTempPath(), $"honua-outside-{Guid.NewGuid():N}.geojson");
        await File.WriteAllTextAsync(outsideTarget, "{}");
        var symlinkInTenantA = Path.Join(tenantADir, "link.geojson");

        try
        {
            var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
            options.CurrentValue.Returns(new GeoprocessingExecutorOptions { ImportStagingDirectory = stagingRoot });
            var executor = new ImportDatasetJobExecutor(
                Substitute.For<IServiceScopeFactory>(),
                NullLogger<ImportDatasetJobExecutor>.Instance,
                options);

            // Tenant A must not access Tenant B's staged file.
            var crossTenantJob = CreateImportJob(tenantBFile, tenantId: "tenant-a");
            var crossResult = await executor.ExecuteAsync(crossTenantJob, Substitute.For<IJobExecutionContext>(), default);
            crossResult.Status.Should().Be(ExecutionJobStatus.Failed);
            crossResult.ErrorMessage.Should().Contain("staging directory");

            // Symlinks inside Tenant A's partition must be rejected.
            File.CreateSymbolicLink(symlinkInTenantA, outsideTarget);
            var symlinkJob = CreateImportJob(symlinkInTenantA, tenantId: "tenant-a");
            var symlinkResult = await executor.ExecuteAsync(symlinkJob, Substitute.For<IJobExecutionContext>(), default);
            symlinkResult.Status.Should().Be(ExecutionJobStatus.Failed);
            symlinkResult.ErrorMessage.Should().Contain("symbolic links");
        }
        finally
        {
            if (File.Exists(outsideTarget))
            {
                File.Delete(outsideTarget);
            }

            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    [UnitTest]
    public void FeatureCollectionArtifact_AndBoundedArtifactWriter_PreserveNon4326Srid()
    {
        var factory = new GeometryFactory(new PrecisionModel(), 3857);
        var feature = new Feature(factory.CreatePoint(new CoordinateZ(1000, 2000, 15.5)), new AttributesTable());
        var written = BoundedArtifactWriter.WriteFeatureCollection([feature], "test.op", 4096, CancellationToken.None);

        using (var doc = JsonDocument.Parse(written))
        {
            var sr = doc.RootElement.GetProperty("honua:spatialReference");
            sr.GetProperty("state").GetString().Should().Be("known");
            sr.GetProperty("srid").GetInt32().Should().Be(3857);
        }

        FeatureCollectionArtifact.TryParseJson(Encoding.UTF8.GetString(written), out var parsed, out var error)
            .Should().BeTrue(error);
        parsed.Should().ContainSingle();
        parsed[0].Geometry!.SRID.Should().Be(3857);
        parsed[0].Geometry!.Coordinate.Z.Should().BeApproximately(15.5, 1e-9);
    }

    [UnitTest]
    public async Task LayerSourcedFeatureExecutor_ReadLayerAsync_Preserves3DZAndOutputSrid()
    {
        var source = Substitute.For<IDagFeatureSource>();
        source.ReadAsync(Arg.Any<DagSourceRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleFeatureAsync(new DagSourceFeature
            {
                GeometryGeoJson = """{"type":"Point","coordinates":[500000,4000000,42.25]}""",
                Attributes = new Dictionary<string, object?>(),
            }));

        var features = await LayerSourcedFeatureExecutor.ReadLayerAsync(
            source,
            new DagSourceRequest { LayerId = 1, OutputSrid = 32610 },
            CancellationToken.None);

        features.Should().ContainSingle();
        features[0].Geometry!.SRID.Should().Be(32610);
        features[0].Geometry!.Coordinate.Z.Should().BeApproximately(42.25, 1e-9);
    }

    [UnitTest]
    public void SpatialJoinSupport_DWithinOnGeographicSrid_EvaluatesGeodesicMeters()
    {
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var target = new Feature(factory.CreatePoint(new Coordinate(0.0, 0.0)), new AttributesTable());
        var near = new Feature(factory.CreatePoint(new Coordinate(0.002, 0.0)), new AttributesTable()); // ~222 m
        var far = new Feature(factory.CreatePoint(new Coordinate(0.02, 0.0)), new AttributesTable());   // ~2226 m
        var index = SpatialJoinSupport.BuildIndex([near, far], CancellationToken.None);

        var joined = SpatialJoinSupport.Join(
            target, index, SpatialJoinPredicate.DWithin, distance: 500.0, [], [], srid: 4326);

        joined.Attributes[SpatialJoinSupport.JoinCountAttribute].Should().Be(1L);
    }

    private static readonly string[] GeodesicMeasureProcessIds =
    [
        "geometry.buffer",
        "geometry.area",
        "geometry.length",
    ];

    [UnitTest]
    public async Task LayerSpatialJoin_RequestsJoinLayerInTargetSrid()
    {
        var capturedRequests = new List<DagSourceRequest>();
        var source = Substitute.For<IDagFeatureSource>();
        source.SourceId.Returns("source.honua-layer");
        source.ReadAsync(Arg.Any<DagSourceRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var req = call.Arg<DagSourceRequest>();
                capturedRequests.Add(req);
                return SingleFeatureAsync(new DagSourceFeature
                {
                    GeometryGeoJson = """{"type":"Point","coordinates":[500000,4000000]}""",
                    Attributes = new Dictionary<string, object?> { ["id"] = req.LayerId },
                });
            });

        await using var provider = new ServiceCollection()
            .AddSingleton<IDagFeatureSource>(source)
            .BuildServiceProvider();
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = new LayerSpatialJoinExecutor(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<LayerSpatialJoinExecutor>.Instance);

        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        var job = new ExecutionJobRecord
        {
            OperationId = "op-sjoin-srid",
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:analytics.spatial-join",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = LayerSpatialJoinExecutor.HandledProcessId,
                    [prefix + "layerId"] = "1",
                    [prefix + "joinLayerId"] = "2",
                    [prefix + "outSrid"] = "32610",
                },
            },
        };

        var result = await executor.ExecuteAsync(job, Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
        capturedRequests.Should().HaveCount(2);
        capturedRequests[1].LayerId.Should().Be(2);
        capturedRequests[1].OutputSrid.Should().Be(32610);
    }

    [UnitTest]
    public void BuiltInProcessCatalog_AndGeodesicOperations_SupportGeodesicAndAntipodalFallback()
    {
        var catalog = new BuiltInProcessCatalog();
        foreach (var id in GeodesicMeasureProcessIds)
        {
            var process = catalog.GetProcess(id);
            process.Should().NotBeNull();
            process!.Parameters.Should().Contain(p => p.Name == "geodesic");
            process.Parameters.Should().Contain(p => p.Name == "unit");
        }

        // Near-antipodal pair where Vincenty fails to converge must fall back to Haversine (~20,000 km).
        var distance = GeodesicGeometryOperations.ComputeEllipsoidalDistanceMeters(0.0, 0.0, 0.0, 179.9999);
        distance.Should().BeGreaterThan(19_900_000.0).And.BeLessThan(20_100_000.0);
    }

    [UnitTest]
    public async Task OverlayClip_ReadsSpilledFeatureStreamSecondaryInput()
    {
        var outputRoot = Path.Join(Path.GetTempPath(), $"honua-overlay-spill-{Guid.NewGuid():N}");
        try
        {
            var factory = new GeometryFactory(new PrecisionModel(), 4326);
            var targetPoly = factory.CreatePolygon([new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0)]);
            var clipPoly = factory.CreatePolygon([new(0, 0), new(5, 0), new(5, 5), new(0, 5), new(0, 0)]);

            var inputPayload = FeatureCollectionArtifact.WriteFeatureCollection(
                [new Feature(targetPoly, new AttributesTable())], "source");
            var inputUri = FeatureCollectionArtifact.BuildDataUri(inputPayload);

            var spillPath = FeatureStreamArtifact.AllocateSpillPath(outputRoot, "op-clip", "clip");
            var clipStreamRef = await FeatureStreamArtifact.WriteStreamAsync(
                spillPath,
                SingleNtsFeatureAsync(new Feature(clipPoly, new AttributesTable())),
                CancellationToken.None);

            var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
            options.CurrentValue.Returns(new GeoprocessingExecutorOptions { OutputRootDirectory = outputRoot });
            var executor = new OverlayClipExecutor(options);

            var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
            var job = new ExecutionJobRecord
            {
                OperationId = "op-overlay-spill",
                Status = ExecutionJobStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Spec = new ExecutionJobSpec
                {
                    Kind = ExecutionJobKind.Geoprocessing,
                    TargetKind = BatchComputeTargetKind.KubernetesJob,
                    Backend = "local",
                    WorkloadName = "geoprocessing:overlay.clip",
                    Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = OverlayClipExecutor.HandledProcessId,
                        [prefix + "input"] = inputUri,
                        [prefix + "clip"] = clipStreamRef,
                    },
                },
            };

            string? published = null;
            var context = Substitute.For<IJobExecutionContext>();
            context.OperationId.Returns(job.OperationId);
            context.When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
                .Do(call => published = call.ArgAt<string>(0));

            var result = await executor.ExecuteAsync(job, context, CancellationToken.None);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            published.Should().NotBeNull();
            FeatureCollectionArtifact.TryParseDataUri(published, out var clipped, out var parseErr).Should().BeTrue(parseErr);
            clipped.Should().ContainSingle();
            clipped[0].Geometry!.Area.Should().BeApproximately(25.0, 1e-6);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    private static ExecutionJobRecord CreateImportJob(string sourcePath, string tenantId)
    {
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        return new ExecutionJobRecord
        {
            OperationId = "op-import-sec",
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Audit = new OperationAuditInfo
            {
                SubmitterSecurityContext = new JobSecurityContext("user-1", tenantId, []),
            },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:import.dataset",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = ImportDatasetJobExecutor.HandledProcessId,
                    [prefix + "connection"] = Guid.NewGuid().ToString(),
                    [prefix + "sourcePath"] = sourcePath,
                    [prefix + "fileName"] = "test.geojson",
                    [prefix + "tableName"] = "test_tbl",
                    [prefix + "layerName"] = "Test Layer",
                },
            },
        };
    }

    private static async IAsyncEnumerable<DagSourceFeature> SingleFeatureAsync(DagSourceFeature feature)
    {
        yield return feature;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<IFeature> SingleNtsFeatureAsync(IFeature feature)
    {
        yield return feature;
        await Task.CompletedTask;
    }
}
