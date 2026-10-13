// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Unit coverage of the slice-4 <c>geometry.centroid</c> executor. Pins the
/// same failure surfaces slice 1/2/3 introduced (unsupported process id,
/// malformed inputs, oversized artifact guardrail) plus the happy-path
/// GeoJSON Point Feature shape produced over a polygon input.
/// </summary>
public sealed class GeometryCentroidJobExecutorTests
{
    private const string FeatureDataUriPrefix = "data:application/geo+json;base64,";

    [UnitTest]
    public async Task ExecuteAsync_UnsupportedProcessId_FailsWithClassifiedMessage()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-test");

        var record = CreateJobRecord(processId: "geometry.buffer", includeInputs: false);
        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("geometry.centroid");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_HappyPath_PublishesCentroidPointFeature()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-centroid");

        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        // Centroid of a 10x10 box anchored at (0,0) is the geometric center (5,5).
        var box = WkbBase64(BuildBoxPolygon(0, 0, 10, 10));
        var record = CreateJobRecord(
            processId: GeometryCentroidJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: box);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        publishedUri.Should().NotBeNull();
        publishedUri!.Should().StartWith(FeatureDataUriPrefix);

        var bytes = Convert.FromBase64String(publishedUri[FeatureDataUriPrefix.Length..]);
        using var doc = JsonDocument.Parse(bytes);
        doc.RootElement.GetProperty("type").GetString().Should().Be("Feature");
        var properties = doc.RootElement.GetProperty("properties");
        properties.GetProperty("processId").GetString().Should().Be("geometry.centroid");
        properties.GetProperty("inputSrid").GetInt32().Should().Be(4326);
        properties.GetProperty("inputGeometryType").GetString().Should().Be("Polygon");

        var geometry = new GeoJsonReader().Read<Geometry>(
            doc.RootElement.GetProperty("geometry").GetRawText());
        geometry.Should().BeOfType<Point>();
        var point = (Point)geometry;
        point.X.Should().BeApproximately(5.0, 1e-9);
        point.Y.Should().BeApproximately(5.0, 1e-9);
    }

    [UnitTest]
    public async Task ExecuteAsync_MissingWkb_FailsCleanly()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-missing");

        var record = CreateJobRecord(
            processId: GeometryCentroidJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: null);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("'wkb'");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_InvalidSrid_FailsCleanly()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-srid");

        var record = CreateJobRecord(
            processId: GeometryCentroidJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: WkbBase64(BuildBoxPolygon(0, 0, 10, 10)),
            srid: "not-an-int");

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("'srid'");
    }

    [UnitTest]
    public async Task ExecuteAsync_PayloadExceedsMaxArtifactBytes_FailsWithGuardrail()
    {
        var executor = CreateExecutor(maxArtifactBytes: 8);
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-too-big");

        var record = CreateJobRecord(
            processId: GeometryCentroidJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: WkbBase64(BuildBoxPolygon(0, 0, 10, 10)));

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("MaxArtifactBytes");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static GeometryCentroidJobExecutor CreateExecutor(long maxArtifactBytes = 50L * 1024L * 1024L)
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = maxArtifactBytes,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return new GeometryCentroidJobExecutor(monitor, NullLogger<GeometryCentroidJobExecutor>.Instance);
    }

    private static Polygon BuildBoxPolygon(double minX, double minY, double maxX, double maxY)
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
        return factory.CreatePolygon(new[]
        {
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY),
        });
    }

    private static string WkbBase64(Geometry geometry)
        => Convert.ToBase64String(new WKBWriter().Write(geometry));

    private static ExecutionJobRecord CreateJobRecord(
        string? processId,
        bool includeInputs,
        string? wkb = null,
        string? srid = "4326")
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(processId))
        {
            parameters[ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = processId;
            parameters["protocolProcessId"] = processId;
        }

        if (includeInputs)
        {
            var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
            if (wkb != null)
            {
                parameters[prefix + "wkb"] = wkb;
            }
            parameters[prefix + "srid"] = srid ?? "4326";
        }

        return new ExecutionJobRecord
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
    }
}
