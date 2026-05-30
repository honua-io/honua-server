// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Server.Features.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Unit coverage of the slice-4 <c>geometry.length</c> executor. Pins the
/// same failure surfaces as the slice-3 area executor (unsupported process
/// id, malformed inputs, oversized artifact guardrail) plus the happy-path
/// scalar measure payload shape for a deterministic 3-4-5 right-triangle
/// linestring with total length 7.
/// </summary>
public sealed class GeometryLengthJobExecutorTests
{
    private const string ScalarDataUriPrefix = "data:application/json;base64,";

    [UnitTest]
    public async Task ExecuteAsync_UnsupportedProcessId_FailsWithClassifiedMessage()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-test");

        var record = CreateJobRecord(processId: "geometry.buffer", includeInputs: false);
        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("geometry.length");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_HappyPath_PublishesMeasureResult()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-length");

        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        // LINESTRING(0 0, 3 0, 3 4) has total planar length 3 + 4 = 7.
        var line = WkbBase64(BuildLineString(
            new Coordinate(0, 0),
            new Coordinate(3, 0),
            new Coordinate(3, 4)));
        var record = CreateJobRecord(
            processId: GeometryLengthJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: line);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        publishedUri.Should().NotBeNull();
        publishedUri!.Should().StartWith(ScalarDataUriPrefix);

        var bytes = Convert.FromBase64String(publishedUri[ScalarDataUriPrefix.Length..]);
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        root.GetProperty("type").GetString().Should().Be("MeasureResult");
        root.GetProperty("processId").GetString().Should().Be("geometry.length");
        root.GetProperty("measure").GetString().Should().Be("length");
        root.GetProperty("value").GetDouble().Should().BeApproximately(7.0, 1e-9);
        root.GetProperty("unit").GetString().Should().Be("input-crs-units");
        root.GetProperty("inputSrid").GetInt32().Should().Be(4326);
        root.GetProperty("inputGeometryType").GetString().Should().Be("LineString");
    }

    [UnitTest]
    public async Task ExecuteAsync_MissingWkb_FailsCleanly()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-missing");

        var record = CreateJobRecord(
            processId: GeometryLengthJobExecutor.HandledProcessId,
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
            processId: GeometryLengthJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: WkbBase64(BuildLineString(new Coordinate(0, 0), new Coordinate(1, 0))),
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
            processId: GeometryLengthJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: WkbBase64(BuildLineString(new Coordinate(0, 0), new Coordinate(1, 0))));

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("MaxArtifactBytes");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static GeometryLengthJobExecutor CreateExecutor(long maxArtifactBytes = 50L * 1024L * 1024L)
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = maxArtifactBytes,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return new GeometryLengthJobExecutor(monitor, NullLogger<GeometryLengthJobExecutor>.Instance);
    }

    private static LineString BuildLineString(params Coordinate[] coordinates)
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
        return factory.CreateLineString(coordinates);
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
