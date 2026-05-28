// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Geoprocessing.Execution;
using Honua.Server.Features.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Unit coverage for the reconciled <c>geometry.difference</c> executor — the
/// managed NetTopologySuite <see cref="Geometry.Difference(Geometry)"/> overlay
/// that closes a catalog-honesty gap (advertised, sync-eligible, but previously
/// unrouted on trunk). Companion to <c>geometry.intersect</c>.
/// </summary>
public sealed class GeometryDifferenceJobExecutorTests
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    [UnitTest]
    public async Task ExecuteAsync_UnsupportedProcessId_FailsWithClassifiedMessage()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-test");

        var record = CreateJobRecord("geometry.intersect", includeInputs: false);
        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("geometry.difference");
    }

    [UnitTest]
    public async Task ExecuteAsync_HappyPath_PublishesDifferenceFeature()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-diff");

        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        // 0..10 box (area 100) minus the 5..15 box overlap (5x5 = 25) leaves
        // an L-shaped remainder with area 75.
        var target = WkbBase64(BuildBoxPolygon(0, 0, 10, 10));
        var eraser = WkbBase64(BuildBoxPolygon(5, 5, 15, 15));

        var record = CreateJobRecord(
            GeometryDifferenceJobExecutor.HandledProcessId,
            includeInputs: true,
            targetWkb: target,
            eraserWkb: eraser);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        publishedUri.Should().NotBeNull();
        publishedUri!.Should().StartWith(DataUriPrefix);

        var bytes = Convert.FromBase64String(publishedUri[DataUriPrefix.Length..]);
        using var doc = JsonDocument.Parse(bytes);
        doc.RootElement.GetProperty("properties").GetProperty("processId").GetString()
            .Should().Be("geometry.difference");

        var geometry = new GeoJsonReader().Read<Geometry>(
            doc.RootElement.GetProperty("geometry").GetRawText());
        geometry.Area.Should().BeApproximately(75.0, 1e-6);
    }

    [UnitTest]
    public async Task ExecuteAsync_EraserCoversTarget_FailsAsEmpty()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-empty");

        var record = CreateJobRecord(
            GeometryDifferenceJobExecutor.HandledProcessId,
            includeInputs: true,
            targetWkb: WkbBase64(BuildBoxPolygon(2, 2, 4, 4)),
            eraserWkb: WkbBase64(BuildBoxPolygon(0, 0, 10, 10)));

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);
        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("empty");
    }

    [UnitTest]
    public async Task ExecuteAsync_MissingEraserWkb_FailsCleanly()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-missing");

        var record = CreateJobRecord(
            GeometryDifferenceJobExecutor.HandledProcessId,
            includeInputs: true,
            targetWkb: WkbBase64(BuildBoxPolygon(0, 0, 1, 1)),
            omitEraser: true);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);
        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("eraserWkb");
    }

    [UnitTest]
    public async Task ExecuteAsync_PayloadExceedsMaxArtifactBytes_FailsWithGuardrail()
    {
        var executor = CreateExecutor(maxArtifactBytes: 64);
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-too-big");

        var record = CreateJobRecord(
            GeometryDifferenceJobExecutor.HandledProcessId,
            includeInputs: true,
            targetWkb: WkbBase64(BuildBoxPolygon(0, 0, 10, 10)),
            eraserWkb: WkbBase64(BuildBoxPolygon(5, 5, 15, 15)));

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("MaxArtifactBytes");
    }

    private static GeometryDifferenceJobExecutor CreateExecutor(long maxArtifactBytes = 50L * 1024L * 1024L)
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = maxArtifactBytes,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return new GeometryDifferenceJobExecutor(monitor, NullLogger<GeometryDifferenceJobExecutor>.Instance);
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
        string? targetWkb = null,
        string? eraserWkb = null,
        bool omitEraser = false)
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
            parameters[prefix + "targetWkb"] = targetWkb ?? string.Empty;
            if (!omitEraser)
            {
                parameters[prefix + "eraserWkb"] = eraserWkb ?? string.Empty;
            }
            parameters[prefix + "srid"] = "4326";
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
