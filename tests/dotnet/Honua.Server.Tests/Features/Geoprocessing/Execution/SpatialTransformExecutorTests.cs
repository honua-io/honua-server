// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Geoprocessing.Execution;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// In-memory unit coverage for the reconciled GeoETL spatial transform executors
/// (transform.spatial-filter, transform.clip, transform.dedup). Uses pure managed
/// NetTopologySuite geometry; no native dependency and no Docker.
/// </summary>
public sealed class SpatialTransformExecutorTests
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    [UnitTest]
    public async Task SpatialFilter_Bbox_Intersects_KeepsOnlyInsideFeatures()
    {
        var executor = new SpatialFilterTransformExecutor(Options());
        var input = BuildInputUri(
            Feature(Point(5, 5), ("id", 1)),
            Feature(Point(50, 50), ("id", 2)));

        var (status, uri) = await RunAsync(
            executor,
            SpatialFilterTransformExecutor.HandledProcessId,
            ("input", input),
            ("bbox", "0,0,10,10"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(1);
        features[0].Attributes.GetOptionalValue("id").Should().Be(1L);
    }

    [UnitTest]
    public async Task SpatialFilter_MissingRegion_FailsCleanly()
    {
        var executor = new SpatialFilterTransformExecutor(Options());
        var (status, _) = await RunAsync(
            executor,
            SpatialFilterTransformExecutor.HandledProcessId,
            ("input", BuildInputUri(Feature(Point(1, 1)))));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task Clip_Bbox_IntersectsGeometry_PreservesAttributes()
    {
        var executor = new ClipTransformExecutor(Options());
        var box = BuildBox(0, 0, 10, 10);
        var input = BuildInputUri(Feature(box, ("label", "x")));

        var (status, uri) = await RunAsync(
            executor,
            ClipTransformExecutor.HandledProcessId,
            ("input", input),
            ("bbox", "0,0,5,5"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(1);
        features[0].Geometry!.Area.Should().BeApproximately(25.0, 1e-6, "the 10x10 box clipped to a 5x5 region has area 25");
        features[0].Attributes.GetOptionalValue("label").Should().Be("x");
    }

    [UnitTest]
    public async Task Clip_FeatureOutsideRegion_Dropped()
    {
        var executor = new ClipTransformExecutor(Options());
        var box = BuildBox(20, 20, 30, 30);
        var input = BuildInputUri(Feature(box, ("label", "y")));

        var (status, uri) = await RunAsync(
            executor,
            ClipTransformExecutor.HandledProcessId,
            ("input", input),
            ("bbox", "0,0,5,5"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        ReadFeatures(uri!).Should().BeEmpty();
    }

    [UnitTest]
    public async Task Dedup_ByKey_DropsLaterDuplicates()
    {
        var executor = new DedupTransformExecutor(Options());
        var input = BuildInputUri(
            Feature(Point(0, 0), ("k", "a")),
            Feature(Point(1, 1), ("k", "a")),
            Feature(Point(2, 2), ("k", "b")));

        var (status, uri) = await RunAsync(
            executor,
            DedupTransformExecutor.HandledProcessId,
            ("input", input),
            ("keys", "k"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        ReadFeatures(uri!).Should().HaveCount(2);
    }

    [UnitTest]
    public async Task Dedup_ByGeometry_DropsCoincidentGeometries()
    {
        var executor = new DedupTransformExecutor(Options());
        var input = BuildInputUri(
            Feature(Point(0, 0), ("id", 1)),
            Feature(Point(0, 0), ("id", 2)),
            Feature(Point(9, 9), ("id", 3)));

        var (status, uri) = await RunAsync(
            executor,
            DedupTransformExecutor.HandledProcessId,
            ("input", input),
            ("geometry", "true"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        ReadFeatures(uri!).Should().HaveCount(2);
    }

    [UnitTest]
    public async Task Dedup_NoKeySpec_FailsCleanly()
    {
        var executor = new DedupTransformExecutor(Options());
        var (status, _) = await RunAsync(
            executor,
            DedupTransformExecutor.HandledProcessId,
            ("input", BuildInputUri(Feature(Point(0, 0)))));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static IOptionsMonitor<GeoprocessingExecutorOptions> Options()
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = 50L * 1024L * 1024L,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return monitor;
    }

    private static Point Point(double x, double y)
        => NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326).CreatePoint(new Coordinate(x, y));

    private static Polygon BuildBox(double minX, double minY, double maxX, double maxY)
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326);
        return factory.CreatePolygon(new[]
        {
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY),
        });
    }

    private static Feature Feature(Geometry geometry, params (string Name, object Value)[] attributes)
    {
        var table = new AttributesTable();
        foreach (var (name, value) in attributes)
        {
            table.Add(name, value);
        }

        return new Feature(geometry, table);
    }

    private static string BuildInputUri(params IFeature[] features)
    {
        var collection = new FeatureCollection();
        foreach (var feature in features)
        {
            collection.Add(feature);
        }

        var json = new GeoJsonWriter().Write(collection);
        return DataUriPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static List<IFeature> ReadFeatures(string dataUri)
    {
        var bytes = Convert.FromBase64String(dataUri[DataUriPrefix.Length..]);
        var json = Encoding.UTF8.GetString(bytes);
        return new GeoJsonReader().Read<FeatureCollection>(json).ToList();
    }

    private static async Task<(ExecutionJobStatus Status, string? Uri)> RunAsync(
        IJobExecutor executor,
        string processId,
        params (string Name, string Value)[] inputs)
    {
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-test");
        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = processId,
            ["protocolProcessId"] = processId,
        };

        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        foreach (var (name, value) in inputs)
        {
            parameters[prefix + name] = value;
        }

        var record = new ExecutionJobRecord
        {
            OperationId = "op-test",
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:test",
                Parameters = parameters
            }
        };

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);
        return (result.Status, publishedUri);
    }
}
