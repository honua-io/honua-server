// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Geoprocessing.LocalRunner;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.LocalRunner;

/// <summary>
/// Unit coverage for the headless <see cref="GeoprocessingLocalRunner"/> (GP Devkit,
/// issue #2123) against a managed geometry op. Proves the runner drives a real
/// <see cref="IProcessExecutor"/> with no Redis/queue/job-store, projecting step-0
/// inputs into the durable parameter contract and surfacing the published artifact,
/// structured logs, status, and wall-clock timing. The GDAL-op path (the actual CLI
/// command surfaced for a native op) is covered offline in
/// <c>Honua.Worker.Gdal.Tests.GeoprocessingLocalRunnerGdalTests</c> against a fake
/// GDAL command runner.
/// </summary>
public sealed class GeoprocessingLocalRunnerTests
{
    // POINT(0 0) — the same WKB the durable-runtime buffer tests use.
    private const string PointWkbBase64 = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA";

    [UnitTest]
    public async Task RunAsync_GeometryBuffer_RunsManagedOpAndReturnsArtifact()
    {
        var runner = new GeoprocessingLocalRunner([CreateBufferExecutor()]);

        var result = await runner.RunAsync(
            GeometryBufferJobExecutor.HandledProcessId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["wkb"] = PointWkbBase64,
                ["srid"] = "4326",
                ["distance"] = "10",
            });

        result.Succeeded.Should().BeTrue(result.ErrorMessage);
        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        result.ProcessId.Should().Be(GeometryBufferJobExecutor.HandledProcessId);
        result.Artifacts.Should().ContainSingle();
        result.Artifacts[0].Should().StartWith("data:application/geo+json;base64,");
        result.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);

        // The buffered feature stamps the requested distance; decode and assert so
        // the test proves the runner returned the real op's artifact, not a stub.
        var json = DecodeDataUri(result.Artifacts[0]);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("type").GetString().Should().Be("Feature");
        doc.RootElement
            .GetProperty("properties")
            .GetProperty("bufferDistance")
            .GetDouble()
            .Should().BeApproximately(10d, 1e-9);
    }

    [UnitTest]
    public async Task RunAsync_Default_OmitsGlassBoxForManagedOp()
    {
        var runner = new GeoprocessingLocalRunner([CreateBufferExecutor()]);

        // No GlassBoxCapture: the default, production-equivalent path.
        var result = await runner.RunAsync(
            GeometryBufferJobExecutor.HandledProcessId,
            BufferInputs());

        result.Succeeded.Should().BeTrue(result.ErrorMessage);
        result.GlassBox.Should().BeNull();
    }

    [UnitTest]
    public async Task RunAsync_GlassBox_RendersTimelineAndPreviewForManagedGeometryOp()
    {
        var runner = new GeoprocessingLocalRunner([CreateBufferExecutor()]);
        var capture = new GlassBoxCapture();

        var result = await runner.RunAsync(
            GeometryBufferJobExecutor.HandledProcessId,
            BufferInputs(),
            capture);

        result.Succeeded.Should().BeTrue(result.ErrorMessage);
        result.GlassBox.Should().NotBeNull();
        var glassBox = result.GlassBox!;

        // A managed geometry op shells out to no GDAL subprocess, so there are no native
        // commands and no scratch dirs — but the timeline and artifact preview still render.
        glassBox.Commands.Should().BeEmpty();
        glassBox.ScratchDirectories.Should().BeEmpty();

        // (c) The artifact preview renders for the geometry op (a GeoJSON Feature).
        glassBox.ArtifactPreviews.Should().ContainSingle();
        var preview = glassBox.ArtifactPreviews[0];
        preview.MediaType.Should().Be("application/geo+json");
        preview.SizeBytes.Should().BeGreaterThan(0);
        preview.Summary.Should().Contain("GeoJSON Feature");

        // Timeline: the buffer executor's reported phases, in order, with timing.
        glassBox.Timeline.Should().NotBeEmpty();
        glassBox.Timeline.Select(p => p.Phase).Should().Contain("Computing buffer geometry");
        glassBox.Timeline.Should().BeInAscendingOrder(p => p.Elapsed);
    }

    private static Dictionary<string, string> BufferInputs() =>
        new(StringComparer.Ordinal)
        {
            ["wkb"] = PointWkbBase64,
            ["srid"] = "4326",
            ["distance"] = "10",
        };

    [UnitTest]
    public async Task RunAsync_UnknownProcessId_FailsWithoutInvokingAnyExecutor()
    {
        var runner = new GeoprocessingLocalRunner([CreateBufferExecutor()]);

        var result = await runner.RunAsync(
            "geometry.does-not-exist",
            new Dictionary<string, string>(StringComparer.Ordinal));

        result.Succeeded.Should().BeFalse();
        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("geometry.does-not-exist");
        result.ErrorMessage.Should().Contain(GeometryBufferJobExecutor.HandledProcessId);
        result.Artifacts.Should().BeEmpty();
    }

    [UnitTest]
    public void AvailableProcessIds_ReflectsRegisteredExecutors()
    {
        var runner = new GeoprocessingLocalRunner([CreateBufferExecutor()]);

        runner.AvailableProcessIds.Should().Contain(GeometryBufferJobExecutor.HandledProcessId);
    }

    [UnitTest]
    public async Task RunAsync_InvalidInputs_SurfacesExecutorClassifiedFailure()
    {
        var runner = new GeoprocessingLocalRunner([CreateBufferExecutor()]);

        // Omit the required 'wkb' input; the executor's own validation must classify it.
        var result = await runner.RunAsync(
            GeometryBufferJobExecutor.HandledProcessId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["srid"] = "4326",
                ["distance"] = "10",
            });

        result.Succeeded.Should().BeFalse();
        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("wkb");
        result.Artifacts.Should().BeEmpty();
    }

    private static GeometryBufferJobExecutor CreateBufferExecutor()
    {
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = 50L * 1024L * 1024L,
            ResultRetention = TimeSpan.FromDays(7),
        });
        return new GeometryBufferJobExecutor(monitor, NullLogger<GeometryBufferJobExecutor>.Instance);
    }

    private static string DecodeDataUri(string dataUri)
    {
        var comma = dataUri.IndexOf(',', StringComparison.Ordinal);
        var base64 = dataUri[(comma + 1)..];
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }
}
