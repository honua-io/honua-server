// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Geoprocessing.Execution;
using Honua.Server.Features.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// In-memory unit coverage for the managed GeoETL file sink executors
/// (sink.geojson-file, sink.quarantine). Each writes to a temp file and publishes a
/// result descriptor. No native dependency, no Docker.
/// </summary>
public sealed class FileSinkExecutorTests
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    [UnitTest]
    public async Task GeoJsonFileSink_WritesFeatureCollectionFile()
    {
        var executor = new GeoJsonFileSinkExecutor(Options());
        var path = Path.Combine(Path.GetTempPath(), $"honua-geoetl-sink-{Guid.NewGuid():N}.geojson");
        try
        {
            var input = BuildInputUri(
                Feature(Point(1, 2), ("name", "a")),
                Feature(Point(3, 4), ("name", "b")));

            var (status, uri) = await RunAsync(
                executor, GeoJsonFileSinkExecutor.HandledProcessId, ("input", input), ("path", path));

            status.Should().Be(ExecutionJobStatus.Succeeded);
            uri.Should().StartWith("data:application/json;base64,");
            File.Exists(path).Should().BeTrue();

            var written = await File.ReadAllTextAsync(path);
            var roundTrip = new GeoJsonReader().Read<FeatureCollection>(written);
            roundTrip.Should().HaveCount(2);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [UnitTest]
    public async Task GeoJsonFileSink_MissingPath_FailsCleanly()
    {
        var executor = new GeoJsonFileSinkExecutor(Options());
        var (status, _) = await RunAsync(
            executor, GeoJsonFileSinkExecutor.HandledProcessId, ("input", BuildInputUri(Feature(Point(0, 0)))));
        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task QuarantineSink_TagsRowsAndNeverThrows()
    {
        var executor = new QuarantineSinkExecutor(Options());
        var path = Path.Combine(Path.GetTempPath(), $"honua-geoetl-dlq-{Guid.NewGuid():N}.geojson");
        try
        {
            var input = BuildInputUri(
                Feature(Point(0, 0), ("err", "bad-value")),
                Feature(Point(1, 1)));

            var (status, uri) = await RunAsync(
                executor, QuarantineSinkExecutor.HandledProcessId,
                ("input", input), ("path", path), ("batchId", "batch-123"));

            status.Should().Be(ExecutionJobStatus.Succeeded);
            uri.Should().StartWith("data:application/json;base64,");

            var written = await File.ReadAllTextAsync(path);
            var roundTrip = new GeoJsonReader().Read<FeatureCollection>(written);
            roundTrip.Should().HaveCount(2);
            roundTrip[0].Attributes.GetOptionalValue("_batch_id").Should().Be("batch-123");
            roundTrip[0].Attributes.Exists("_quarantine_reason").Should().BeTrue();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
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
