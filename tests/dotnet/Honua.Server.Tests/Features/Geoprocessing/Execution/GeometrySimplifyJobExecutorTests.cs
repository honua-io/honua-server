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
/// Unit coverage of the slice-5 <c>geometry.simplify</c> executor. Pins
/// the same failure surfaces as the earlier vector executors plus the
/// happy-path GeoJSON Feature shape: a near-straight zig-zag line with a
/// 0.001-unit perturbation collapses to the two-vertex segment under a
/// tolerance of 1.0 (Douglas-Peucker removes the below-tolerance vertex).
/// </summary>
public sealed class GeometrySimplifyJobExecutorTests
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
        result.ErrorMessage.Should().Contain("geometry.simplify");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_HappyPath_CollapsesBelowToleranceVertex()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-simplify");

        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        // LINESTRING(0 0, 5 0.001, 10 0) — the midpoint sits 0.001 units off
        // the (0,0)-(10,0) baseline. Under tolerance 1.0 the Douglas-Peucker
        // walk drops it, leaving the two-vertex segment.
        var line = BuildLineString(
            new Coordinate(0, 0),
            new Coordinate(5, 0.001),
            new Coordinate(10, 0));

        var record = CreateJobRecord(
            processId: GeometrySimplifyJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: WkbBase64(line),
            tolerance: "1.0");

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        publishedUri.Should().NotBeNull();
        publishedUri!.Should().StartWith(FeatureDataUriPrefix);

        var bytes = Convert.FromBase64String(publishedUri[FeatureDataUriPrefix.Length..]);
        using var doc = JsonDocument.Parse(bytes);
        doc.RootElement.GetProperty("type").GetString().Should().Be("Feature");
        var properties = doc.RootElement.GetProperty("properties");
        properties.GetProperty("processId").GetString().Should().Be("geometry.simplify");
        properties.GetProperty("inputSrid").GetInt32().Should().Be(4326);
        properties.GetProperty("tolerance").GetDouble().Should().Be(1.0);
        properties.GetProperty("preserveTopology").GetBoolean().Should().BeTrue();
        properties.GetProperty("inputGeometryType").GetString().Should().Be("LineString");

        var geometry = new GeoJsonReader().Read<Geometry>(
            doc.RootElement.GetProperty("geometry").GetRawText());
        geometry.Should().BeOfType<LineString>();
        ((LineString)geometry).NumPoints.Should().Be(2,
            "the 0.001-offset midpoint is well below the 1.0 tolerance and should collapse");
    }

    [UnitTest]
    public async Task ExecuteAsync_MissingTolerance_FailsCleanly()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-missing-tol");

        var record = CreateJobRecord(
            processId: GeometrySimplifyJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: WkbBase64(BuildLineString(new Coordinate(0, 0), new Coordinate(1, 1))),
            tolerance: null);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("'tolerance'");
    }

    [UnitTest]
    public async Task ExecuteAsync_NegativeTolerance_FailsCleanly()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-neg-tol");

        var record = CreateJobRecord(
            processId: GeometrySimplifyJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: WkbBase64(BuildLineString(new Coordinate(0, 0), new Coordinate(1, 1))),
            tolerance: "-0.5");

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("'tolerance'");
    }

    [UnitTest]
    public async Task ExecuteAsync_MissingWkb_FailsCleanly()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-missing-wkb");

        var record = CreateJobRecord(
            processId: GeometrySimplifyJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: null,
            tolerance: "1.0");

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("'wkb'");
    }

    [UnitTest]
    public async Task ExecuteAsync_PayloadExceedsMaxArtifactBytes_FailsWithGuardrail()
    {
        var executor = CreateExecutor(maxArtifactBytes: 8);
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-too-big");

        var record = CreateJobRecord(
            processId: GeometrySimplifyJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: WkbBase64(BuildLineString(
                new Coordinate(0, 0),
                new Coordinate(10, 0),
                new Coordinate(20, 5),
                new Coordinate(30, 0))),
            tolerance: "0.1");

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("MaxArtifactBytes");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static GeometrySimplifyJobExecutor CreateExecutor(long maxArtifactBytes = 50L * 1024L * 1024L)
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = maxArtifactBytes,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return new GeometrySimplifyJobExecutor(monitor, NullLogger<GeometrySimplifyJobExecutor>.Instance);
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
        string? srid = "4326",
        string? tolerance = "1.0",
        string? preserveTopology = null)
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
            if (tolerance != null)
            {
                parameters[prefix + "tolerance"] = tolerance;
            }
            if (preserveTopology != null)
            {
                parameters[prefix + "preserveTopology"] = preserveTopology;
            }
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
