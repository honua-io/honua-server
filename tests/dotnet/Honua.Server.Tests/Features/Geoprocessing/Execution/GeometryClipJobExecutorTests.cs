// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Geoprocessing.Execution;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Unit coverage of the slice-2 <c>geometry.clip</c> executor. Pins the same
/// failure surfaces slice 1 introduced for <c>geometry.buffer</c> (unsupported
/// process id, malformed inputs, oversized artifact guardrail) plus the
/// happy-path produced GeoJSON Feature shape.
/// </summary>
public sealed class GeometryClipJobExecutorTests
{
    [UnitTest]
    public async Task ExecuteAsync_UnsupportedProcessId_FailsWithClassifiedMessage()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-test");

        var record = CreateJobRecord(processId: "geometry.buffer", includeInputs: false);
        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("geometry.clip");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_HappyPath_PublishesClippedFeature()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-clip");

        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        // Target polygon: 0,0 → 10,10. Clip envelope polygon: 5,5 → 15,15.
        // Expected intersection: 5,5 → 10,10 square.
        var target = WkbBase64(BuildBoxPolygon(0, 0, 10, 10));
        var clip = WkbBase64(BuildBoxPolygon(5, 5, 15, 15));

        var record = CreateJobRecord(
            processId: GeometryClipJobExecutor.HandledProcessId,
            includeInputs: true,
            targetWkb: target,
            clipWkb: clip);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        publishedUri.Should().NotBeNull();
        publishedUri!.Should().StartWith("data:application/geo+json;base64,");

        var base64 = publishedUri["data:application/geo+json;base64,".Length..];
        using var doc = JsonDocument.Parse(Convert.FromBase64String(base64));
        doc.RootElement.GetProperty("type").GetString().Should().Be("Feature");
        doc.RootElement.GetProperty("properties").GetProperty("processId").GetString()
            .Should().Be("geometry.clip");

        var geometry = new GeoJsonReader().Read<Geometry>(
            doc.RootElement.GetProperty("geometry").GetRawText());
        geometry.Area.Should().BeApproximately(25.0, 1e-6,
            "clipping a 10x10 box with a 5,5 → 15,15 envelope yields a 5x5 square");
    }

    [UnitTest]
    public async Task ExecuteAsync_NonOverlapping_FailsAsEmpty()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-empty");

        var target = WkbBase64(BuildBoxPolygon(0, 0, 1, 1));
        var clip = WkbBase64(BuildBoxPolygon(10, 10, 20, 20));

        var record = CreateJobRecord(
            processId: GeometryClipJobExecutor.HandledProcessId,
            includeInputs: true,
            targetWkb: target,
            clipWkb: clip);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);
        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("empty");
    }

    [UnitTest]
    public async Task ExecuteAsync_MissingTargetWkb_FailsCleanly()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-missing");

        var record = CreateJobRecord(
            processId: GeometryClipJobExecutor.HandledProcessId,
            includeInputs: true,
            omitTarget: true,
            clipWkb: WkbBase64(BuildBoxPolygon(0, 0, 1, 1)));

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("targetWkb");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_PayloadExceedsMaxArtifactBytes_FailsWithGuardrail()
    {
        var executor = CreateExecutor(maxArtifactBytes: 64);
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-too-big");

        var record = CreateJobRecord(
            processId: GeometryClipJobExecutor.HandledProcessId,
            includeInputs: true,
            targetWkb: WkbBase64(BuildBoxPolygon(0, 0, 10, 10)),
            clipWkb: WkbBase64(BuildBoxPolygon(5, 5, 15, 15)));

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("MaxArtifactBytes");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static GeometryClipJobExecutor CreateExecutor(long maxArtifactBytes = 50L * 1024L * 1024L)
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = maxArtifactBytes,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return new GeometryClipJobExecutor(monitor, NullLogger<GeometryClipJobExecutor>.Instance);
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
    {
        var writer = new WKBWriter();
        return Convert.ToBase64String(writer.Write(geometry));
    }

    private static ExecutionJobRecord CreateJobRecord(
        string? processId,
        bool includeInputs,
        string? targetWkb = null,
        string? clipWkb = null,
        bool omitTarget = false)
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
            if (!omitTarget)
            {
                parameters[prefix + "targetWkb"] = targetWkb ?? string.Empty;
            }
            parameters[prefix + "clipEnvelopeWkb"] = clipWkb ?? string.Empty;
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
