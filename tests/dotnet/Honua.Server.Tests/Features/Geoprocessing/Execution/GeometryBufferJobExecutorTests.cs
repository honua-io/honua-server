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
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Direct executor coverage that does not require the durable runtime. These
/// tests pin the failure surfaces called out by issue #1031 (slice 1):
/// unsupported process ids must fail cleanly with a process-classified error
/// instead of falling through to <c>NoExecutorForKind</c>, and oversized
/// artifacts must trip the configured guardrail before publication.
/// </summary>
public sealed class GeometryBufferJobExecutorTests
{
    // POINT(0 0) — same WKB the durable-runtime tests use.
    private const string PointWkbBase64 = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA";

    [UnitTest]
    public async Task ExecuteAsync_UnsupportedProcessId_FailsWithClassifiedMessage()
    {
        var executor = CreateExecutor(out _);
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-test");

        var record = CreateJobRecord(
            processId: "geometry.simplify",
            includeInputs: false);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.ErrorMessage.Should().Contain("geometry.buffer");
        result.ErrorMessage.Should().NotContain("No executor");

        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_MissingProcessId_FailsCleanly()
    {
        var executor = CreateExecutor(out _);
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-test");

        var record = CreateJobRecord(processId: null, includeInputs: false);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("geometry.buffer");
    }

    [UnitTest]
    public async Task ExecuteAsync_HappyPath_PublishesDataUriArtifact()
    {
        var executor = CreateExecutor(out _);
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-happy");

        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        var record = CreateJobRecord(
            processId: GeometryBufferJobExecutor.HandledProcessId,
            includeInputs: true,
            distance: "10");

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        publishedUri.Should().NotBeNull();
        publishedUri!.Should().StartWith("data:application/geo+json;base64,");

        var base64 = publishedUri["data:application/geo+json;base64,".Length..];
        var bytes = Convert.FromBase64String(base64);
        using var doc = JsonDocument.Parse(bytes);
        doc.RootElement.GetProperty("type").GetString().Should().Be("Feature");
        doc.RootElement.GetProperty("properties").GetProperty("processId").GetString()
            .Should().Be("geometry.buffer");
        doc.RootElement.GetProperty("properties").GetProperty("bufferDistance").GetDouble()
            .Should().BeApproximately(10d, 1e-9);
    }

    [UnitTest]
    public async Task ExecuteAsync_GeodesicRequested_RejectsAsManualReview()
    {
        var executor = CreateExecutor(out _);
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-geo");

        var record = CreateJobRecord(
            processId: GeometryBufferJobExecutor.HandledProcessId,
            includeInputs: true,
            geodesic: "true");

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("Geodesic", "the first-slice executor only supports CRS-units buffering");
    }

    [UnitTest]
    public async Task ExecuteAsync_MissingWkb_FailsBeforeBufferComputation()
    {
        var executor = CreateExecutor(out _);
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-missing");

        var record = CreateJobRecord(
            processId: GeometryBufferJobExecutor.HandledProcessId,
            includeInputs: true,
            omitWkb: true);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("wkb");

        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_PayloadExceedsMaxArtifactBytes_FailsWithGuardrail()
    {
        // 64 byte cap is below the smallest possible buffered Feature payload.
        var executor = CreateExecutor(out _, maxArtifactBytes: 64);
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-too-big");

        var record = CreateJobRecord(
            processId: GeometryBufferJobExecutor.HandledProcessId,
            includeInputs: true,
            distance: "100");

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("MaxArtifactBytes");

        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_Cancellation_PropagatesOperationCanceled()
    {
        var executor = CreateExecutor(out _);
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-cancel");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var record = CreateJobRecord(
            processId: GeometryBufferJobExecutor.HandledProcessId,
            includeInputs: true);

        var act = async () => await executor.ExecuteAsync(record, context, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static GeometryBufferJobExecutor CreateExecutor(
        out IOptionsMonitor<GeoprocessingExecutorOptions> monitor,
        long maxArtifactBytes = 50L * 1024L * 1024L)
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = maxArtifactBytes,
            ResultRetention = TimeSpan.FromDays(7)
        };
        monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return new GeometryBufferJobExecutor(monitor, NullLogger<GeometryBufferJobExecutor>.Instance);
    }

    private static ExecutionJobRecord CreateJobRecord(
        string? processId,
        bool includeInputs,
        string distance = "25.5",
        string? geodesic = null,
        bool omitWkb = false)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!string.IsNullOrEmpty(processId))
        {
            parameters[ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = processId;
            parameters["protocolProcessId"] = processId;
        }

        if (includeInputs)
        {
            if (!omitWkb)
            {
                parameters[$"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.wkb"] = PointWkbBase64;
            }
            parameters[$"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.srid"] = "4326";
            parameters[$"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.distance"] = distance;
            if (!string.IsNullOrEmpty(geodesic))
            {
                parameters[$"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.geodesic"] = geodesic;
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
