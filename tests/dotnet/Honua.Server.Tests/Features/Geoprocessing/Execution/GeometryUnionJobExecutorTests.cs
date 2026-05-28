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
/// Unit coverage of the slice-3 <c>geometry.union</c> executor. Mirrors the
/// failure surfaces the buffer / clip / intersect / project executors pin
/// (unsupported process id, malformed inputs, oversized artifact guardrail)
/// plus the happy-path produced GeoJSON Feature shape for the aggregate
/// output.
/// </summary>
public sealed class GeometryUnionJobExecutorTests
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
        result.ErrorMessage.Should().Contain("geometry.union");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_HappyPath_PublishesUnionedFeature()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-union");

        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        // Two overlapping 10x10 boxes (offset by 5,5) yield an L-shaped polygon
        // with area 100 + 100 - 25 (overlap) = 175.
        var wkbs = BuildWkbsArrayJson(
            BuildBoxPolygon(0, 0, 10, 10),
            BuildBoxPolygon(5, 5, 15, 15));

        var record = CreateJobRecord(
            processId: GeometryUnionJobExecutor.HandledProcessId,
            includeInputs: true,
            wkbsJson: wkbs);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        publishedUri.Should().NotBeNull();
        publishedUri!.Should().StartWith("data:application/geo+json;base64,");

        var bytes = Convert.FromBase64String(publishedUri["data:application/geo+json;base64,".Length..]);
        using var doc = JsonDocument.Parse(bytes);
        doc.RootElement.GetProperty("type").GetString().Should().Be("Feature");
        var properties = doc.RootElement.GetProperty("properties");
        properties.GetProperty("processId").GetString().Should().Be("geometry.union");
        properties.GetProperty("inputSrid").GetInt32().Should().Be(4326);
        properties.GetProperty("inputCount").GetInt32().Should().Be(2);

        var geometry = new GeoJsonReader().Read<Geometry>(
            doc.RootElement.GetProperty("geometry").GetRawText());
        geometry.Area.Should().BeApproximately(175.0, 1e-6,
            "union of two 10x10 boxes overlapping in a 5x5 square has area 100 + 100 - 25");
    }

    [UnitTest]
    public async Task ExecuteAsync_SingleGeometry_PassesThrough()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-single");

        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        var wkbs = BuildWkbsArrayJson(BuildBoxPolygon(0, 0, 10, 10));
        var record = CreateJobRecord(
            processId: GeometryUnionJobExecutor.HandledProcessId,
            includeInputs: true,
            wkbsJson: wkbs);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        publishedUri.Should().NotBeNull();

        var bytes = Convert.FromBase64String(publishedUri!["data:application/geo+json;base64,".Length..]);
        using var doc = JsonDocument.Parse(bytes);
        var geometry = new GeoJsonReader().Read<Geometry>(
            doc.RootElement.GetProperty("geometry").GetRawText());
        geometry.Area.Should().BeApproximately(100.0, 1e-6);
    }

    [UnitTest]
    public async Task ExecuteAsync_MissingWkbs_FailsCleanly()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-missing");

        var record = CreateJobRecord(
            processId: GeometryUnionJobExecutor.HandledProcessId,
            includeInputs: true,
            wkbsJson: null);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("'wkbs'");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_EmptyArray_FailsCleanly()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-empty-array");

        var record = CreateJobRecord(
            processId: GeometryUnionJobExecutor.HandledProcessId,
            includeInputs: true,
            wkbsJson: "[]");

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("at least one");
    }

    [UnitTest]
    public async Task ExecuteAsync_InvalidJson_FailsCleanly()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-bad-json");

        var record = CreateJobRecord(
            processId: GeometryUnionJobExecutor.HandledProcessId,
            includeInputs: true,
            wkbsJson: "not-json");

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("'wkbs'");
    }

    [UnitTest]
    public async Task ExecuteAsync_PayloadExceedsMaxArtifactBytes_FailsWithGuardrail()
    {
        var executor = CreateExecutor(maxArtifactBytes: 32);
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-too-big");

        var wkbs = BuildWkbsArrayJson(
            BuildBoxPolygon(0, 0, 10, 10),
            BuildBoxPolygon(5, 5, 15, 15));

        var record = CreateJobRecord(
            processId: GeometryUnionJobExecutor.HandledProcessId,
            includeInputs: true,
            wkbsJson: wkbs);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("MaxArtifactBytes");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static GeometryUnionJobExecutor CreateExecutor(long maxArtifactBytes = 50L * 1024L * 1024L)
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = maxArtifactBytes,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return new GeometryUnionJobExecutor(monitor, NullLogger<GeometryUnionJobExecutor>.Instance);
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

    private static string BuildWkbsArrayJson(params Geometry[] geometries)
    {
        var writer = new WKBWriter();
        var items = geometries.Select(g => Convert.ToBase64String(writer.Write(g)));
        return JsonSerializer.Serialize(items);
    }

    private static ExecutionJobRecord CreateJobRecord(
        string? processId,
        bool includeInputs,
        string? wkbsJson = null,
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
            if (wkbsJson != null)
            {
                parameters[prefix + "wkbs"] = wkbsJson;
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
