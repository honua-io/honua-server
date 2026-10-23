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
/// Unit coverage of the slice-2 <c>geometry.intersect</c> executor.
/// </summary>
public sealed class GeometryIntersectJobExecutorTests
{
    [UnitTest]
    public async Task ExecuteAsync_UnsupportedProcessId_FailsWithClassifiedMessage()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-test");

        var record = CreateJobRecord("geometry.clip", includeInputs: false);
        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("geometry.intersect");
    }

    [UnitTest]
    public async Task ExecuteAsync_HappyPath_PublishesIntersectionFeature()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-int");

        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        // Overlap of 0..10 box with 5..15 box is a 5x5 square — area 25.
        var target = WkbBase64(BuildBoxPolygon(0, 0, 10, 10));
        var intersector = WkbBase64(BuildBoxPolygon(5, 5, 15, 15));

        var record = CreateJobRecord(
            GeometryIntersectJobExecutor.HandledProcessId,
            includeInputs: true,
            targetWkb: target,
            intersectorWkb: intersector);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        publishedUri.Should().NotBeNull();
        publishedUri!.Should().StartWith("data:application/geo+json;base64,");

        var bytes = Convert.FromBase64String(publishedUri["data:application/geo+json;base64,".Length..]);
        using var doc = JsonDocument.Parse(bytes);
        doc.RootElement.GetProperty("properties").GetProperty("processId").GetString()
            .Should().Be("geometry.intersect");

        var geometry = new GeoJsonReader().Read<Geometry>(
            doc.RootElement.GetProperty("geometry").GetRawText());
        geometry.Area.Should().BeApproximately(25.0, 1e-6);
    }

    [UnitTest]
    public async Task ExecuteAsync_NonOverlapping_FailsAsEmpty()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-empty");

        var record = CreateJobRecord(
            GeometryIntersectJobExecutor.HandledProcessId,
            includeInputs: true,
            targetWkb: WkbBase64(BuildBoxPolygon(0, 0, 1, 1)),
            intersectorWkb: WkbBase64(BuildBoxPolygon(10, 10, 20, 20)));

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);
        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("empty");
    }

    [UnitTest]
    public async Task ExecuteAsync_MissingIntersectorWkb_FailsCleanly()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-missing");

        var record = CreateJobRecord(
            GeometryIntersectJobExecutor.HandledProcessId,
            includeInputs: true,
            targetWkb: WkbBase64(BuildBoxPolygon(0, 0, 1, 1)),
            omitIntersector: true);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);
        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("intersectorWkb");
    }

    [UnitTest]
    public async Task ExecuteAsync_PayloadExceedsMaxArtifactBytes_FailsWithGuardrail()
    {
        var executor = CreateExecutor(maxArtifactBytes: 64);
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-too-big");

        var record = CreateJobRecord(
            GeometryIntersectJobExecutor.HandledProcessId,
            includeInputs: true,
            targetWkb: WkbBase64(BuildBoxPolygon(0, 0, 10, 10)),
            intersectorWkb: WkbBase64(BuildBoxPolygon(5, 5, 15, 15)));

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("MaxArtifactBytes");
    }

    private static GeometryIntersectJobExecutor CreateExecutor(long maxArtifactBytes = 50L * 1024L * 1024L)
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = maxArtifactBytes,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return new GeometryIntersectJobExecutor(monitor, NullLogger<GeometryIntersectJobExecutor>.Instance);
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
        string? intersectorWkb = null,
        bool omitIntersector = false)
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
            if (!omitIntersector)
            {
                parameters[prefix + "intersectorWkb"] = intersectorWkb ?? string.Empty;
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
