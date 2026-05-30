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
/// Unit coverage of the slice-5 <c>geometry.snap</c> executor. Pins the
/// failure surfaces shared by the other vector executors (unsupported
/// process id, missing inputs, oversized artifact) and the happy path:
/// a near-coincident input point is pulled onto the reference origin
/// when the tolerance exceeds their separation, matching the behavior
/// of NetTopologySuite's <c>GeometrySnapper.SnapTo</c>.
/// </summary>
public sealed class GeometrySnapJobExecutorTests
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
        result.ErrorMessage.Should().Contain("geometry.snap");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_HappyPath_SnapsPointToReferenceVertex()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-snap");

        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        // Input POINT(0.1 0.1) is 0.14-ish from POINT(0 0); a tolerance of
        // 0.5 covers the gap so the snapper pulls the input onto the
        // reference vertex.
        var input = BuildPoint(0.1, 0.1);
        var reference = BuildPoint(0, 0);

        var record = CreateJobRecord(
            processId: GeometrySnapJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: WkbBase64(input),
            referenceWkb: WkbBase64(reference),
            tolerance: "0.5");

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        publishedUri.Should().NotBeNull();
        publishedUri!.Should().StartWith(FeatureDataUriPrefix);

        var bytes = Convert.FromBase64String(publishedUri[FeatureDataUriPrefix.Length..]);
        using var doc = JsonDocument.Parse(bytes);
        doc.RootElement.GetProperty("type").GetString().Should().Be("Feature");
        var properties = doc.RootElement.GetProperty("properties");
        properties.GetProperty("processId").GetString().Should().Be("geometry.snap");
        properties.GetProperty("inputSrid").GetInt32().Should().Be(4326);
        properties.GetProperty("tolerance").GetDouble().Should().Be(0.5);
        properties.GetProperty("inputGeometryType").GetString().Should().Be("Point");
        properties.GetProperty("referenceGeometryType").GetString().Should().Be("Point");

        var geometry = new GeoJsonReader().Read<Geometry>(
            doc.RootElement.GetProperty("geometry").GetRawText());
        geometry.Should().BeOfType<Point>();
        var snapped = (Point)geometry;
        snapped.X.Should().BeApproximately(0.0, 1e-9, "input should be pulled onto the reference vertex");
        snapped.Y.Should().BeApproximately(0.0, 1e-9);
    }

    [UnitTest]
    public async Task ExecuteAsync_BeyondTolerance_LeavesGeometryUnchanged()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-no-snap");

        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        // Input is 1.0 unit away from reference; tolerance 0.1 is too small
        // to trigger snapping, so the executor must succeed but leave the
        // input coordinates intact.
        var input = BuildPoint(1.0, 0.0);
        var reference = BuildPoint(0, 0);

        var record = CreateJobRecord(
            processId: GeometrySnapJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: WkbBase64(input),
            referenceWkb: WkbBase64(reference),
            tolerance: "0.1");

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        publishedUri.Should().NotBeNull();

        var bytes = Convert.FromBase64String(publishedUri!["data:application/geo+json;base64,".Length..]);
        using var doc = JsonDocument.Parse(bytes);
        var geometry = new GeoJsonReader().Read<Geometry>(
            doc.RootElement.GetProperty("geometry").GetRawText());
        ((Point)geometry).X.Should().BeApproximately(1.0, 1e-9);
    }

    [UnitTest]
    public async Task ExecuteAsync_MissingReferenceWkb_FailsCleanly()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-missing-ref");

        var record = CreateJobRecord(
            processId: GeometrySnapJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: WkbBase64(BuildPoint(0, 0)),
            referenceWkb: null,
            tolerance: "0.5");

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("'referenceWkb'");
    }

    [UnitTest]
    public async Task ExecuteAsync_InvalidTolerance_FailsCleanly()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-bad-tol");

        var record = CreateJobRecord(
            processId: GeometrySnapJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: WkbBase64(BuildPoint(0, 0)),
            referenceWkb: WkbBase64(BuildPoint(1, 1)),
            tolerance: "not-a-number");

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("'tolerance'");
    }

    [UnitTest]
    public async Task ExecuteAsync_PayloadExceedsMaxArtifactBytes_FailsWithGuardrail()
    {
        var executor = CreateExecutor(maxArtifactBytes: 8);
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-too-big");

        var record = CreateJobRecord(
            processId: GeometrySnapJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: WkbBase64(BuildPoint(0.1, 0.1)),
            referenceWkb: WkbBase64(BuildPoint(0, 0)),
            tolerance: "0.5");

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("MaxArtifactBytes");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static GeometrySnapJobExecutor CreateExecutor(long maxArtifactBytes = 50L * 1024L * 1024L)
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = maxArtifactBytes,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return new GeometrySnapJobExecutor(monitor, NullLogger<GeometrySnapJobExecutor>.Instance);
    }

    private static Point BuildPoint(double x, double y)
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
        return factory.CreatePoint(new Coordinate(x, y));
    }

    private static string WkbBase64(Geometry geometry)
        => Convert.ToBase64String(new WKBWriter().Write(geometry));

    private static ExecutionJobRecord CreateJobRecord(
        string? processId,
        bool includeInputs,
        string? wkb = null,
        string? referenceWkb = null,
        string? srid = "4326",
        string? tolerance = "0.5")
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
            if (referenceWkb != null)
            {
                parameters[prefix + "referenceWkb"] = referenceWkb;
            }
            parameters[prefix + "srid"] = srid ?? "4326";
            if (tolerance != null)
            {
                parameters[prefix + "tolerance"] = tolerance;
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
