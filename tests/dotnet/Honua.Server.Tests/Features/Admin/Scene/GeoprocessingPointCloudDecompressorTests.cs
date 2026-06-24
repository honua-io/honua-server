// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Server.Features.Admin.Scene;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Admin.Scene;

/// <summary>
/// Fake-driven coverage for <see cref="GeoprocessingPointCloudDecompressor"/>
/// (#1854): the server-side auto-dispatch that submits the canonical
/// <c>pcloud.translate</c> worker plan, polls the job to a terminal state, and
/// returns the decompressed uncompressed-LAS bytes. A fake job service stands in
/// for the durable job runtime and the out-of-tree PDAL worker, so the
/// dispatch/detection logic is verified without Redis or a live PDAL install —
/// mirroring the worker-side <c>PdalPointCloudConvertExecutorTests</c>
/// fake-runner pattern. The poll interval is driven to near-zero so the tests run
/// in milliseconds against the real clock.
/// </summary>
public sealed class GeoprocessingPointCloudDecompressorTests
{
    private static readonly byte[] DecompressedLas = Encoding.UTF8.GetBytes("LASF-fake-decompressed");

    [UnitTest]
    public async Task DecompressAsync_SucceededJob_DecodesLasArtifactAndSubmitsNativeTranslatePlan()
    {
        var job = new FakeGeoprocessingJobService(Statuses(ExecutionJobStatus.Succeeded), LasArtifact(DecompressedLas));
        var sut = NewDecompressor(job);

        var result = await sut.DecompressAsync(Encoding.UTF8.GetBytes("fake-laz"), sourceSrs: null, CancellationToken.None);

        result.Should().Equal(DecompressedLas);
        job.SubmittedPlan.Should().NotBeNull();
        var step = job.SubmittedPlan!.Steps.Should().ContainSingle().Subject;
        step.ProcessId.Should().Be(GeoprocessingPointCloudDecompressor.ProcessId);
        step.Kind.Should().Be(AnalysisPlanStepKind.Geoprocess);
        step.Inputs.Should().ContainKey("source");
        step.Inputs.Should().NotContainKey("sourceSrs");
        job.AuthorizedFor.Should().Be((OperatorResourceType.Job, OperatorOperation.Create));
    }

    [UnitTest]
    public async Task DecompressAsync_ProjectedSourceSrs_ForwardsSourceSrsInput()
    {
        var job = new FakeGeoprocessingJobService(Statuses(ExecutionJobStatus.Succeeded), LasArtifact(DecompressedLas));
        var sut = NewDecompressor(job);

        _ = await sut.DecompressAsync(Encoding.UTF8.GetBytes("fake-laz"), sourceSrs: "EPSG:32610", CancellationToken.None);

        job.SubmittedPlan!.Steps[0].Inputs.Should().Contain(new KeyValuePair<string, string>("sourceSrs", "EPSG:32610"));
    }

    [UnitTest]
    public async Task DecompressAsync_PollsUntilTerminal()
    {
        // Queued -> Running -> Succeeded: the dispatcher must poll past the
        // non-terminal states before reading the artifact.
        var job = new FakeGeoprocessingJobService(
            Statuses(ExecutionJobStatus.Queued, ExecutionJobStatus.Running, ExecutionJobStatus.Succeeded),
            LasArtifact(DecompressedLas));
        var sut = NewDecompressor(job);

        var result = await sut.DecompressAsync(Encoding.UTF8.GetBytes("fake-laz"), null, CancellationToken.None);

        result.Should().Equal(DecompressedLas);
        job.GetJobCalls.Should().BeGreaterThanOrEqualTo(2);
    }

    [UnitTest]
    public async Task DecompressAsync_FailedJob_ThrowsDecompressionException()
    {
        var job = new FakeGeoprocessingJobService(Statuses(ExecutionJobStatus.Failed), artifactUri: null);
        var sut = NewDecompressor(job);

        var act = () => sut.DecompressAsync(Encoding.UTF8.GetBytes("fake-laz"), null, CancellationToken.None);

        await act.Should().ThrowAsync<PointCloudDecompressionException>()
            .Where(e => e.Message.Contains("Failed", StringComparison.Ordinal));
    }

    [UnitTest]
    public async Task DecompressAsync_SucceededButNoLasArtifact_ThrowsDecompressionException()
    {
        var job = new FakeGeoprocessingJobService(
            Statuses(ExecutionJobStatus.Succeeded),
            artifactUri: "data:application/octet-stream;base64,QUJD"); // not a LAS artifact
        var sut = NewDecompressor(job);

        var act = () => sut.DecompressAsync(Encoding.UTF8.GetBytes("fake-laz"), null, CancellationToken.None);

        await act.Should().ThrowAsync<PointCloudDecompressionException>()
            .Where(e => e.Message.Contains("no uncompressed-LAS artifact", StringComparison.Ordinal));
    }

    [UnitTest]
    public async Task DecompressAsync_SubmitThrows_WrappedAsDecompressionException()
    {
        var job = new FakeGeoprocessingJobService(Statuses(ExecutionJobStatus.Succeeded), LasArtifact(DecompressedLas))
        {
            SubmitException = new InvalidOperationException("queue down"),
        };
        var sut = NewDecompressor(job);

        var act = () => sut.DecompressAsync(Encoding.UTF8.GetBytes("fake-laz"), null, CancellationToken.None);

        await act.Should().ThrowAsync<PointCloudDecompressionException>()
            .Where(e => e.Message.Contains("submit", StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    public async Task DecompressAsync_NeverTerminal_TimesOut()
    {
        var job = new FakeGeoprocessingJobService(Statuses(), LasArtifact(DecompressedLas)); // always Running
        var sut = NewDecompressor(job, timeout: TimeSpan.FromMilliseconds(40));

        var act = () => sut.DecompressAsync(Encoding.UTF8.GetBytes("fake-laz"), null, CancellationToken.None);

        await act.Should().ThrowAsync<PointCloudDecompressionException>()
            .Where(e => e.Message.Contains("did not complete", StringComparison.Ordinal));
    }

    [UnitTest]
    public async Task DecompressAsync_EmptySource_Throws()
    {
        var job = new FakeGeoprocessingJobService(Statuses(ExecutionJobStatus.Succeeded), LasArtifact(DecompressedLas));
        var sut = NewDecompressor(job);

        var act = () => sut.DecompressAsync([], null, CancellationToken.None);

        await act.Should().ThrowAsync<PointCloudDecompressionException>();
    }

    [UnitTest]
    public void TryDecodeLasDataUri_RoundTripsLasPayload()
    {
        var uri = LasArtifact(DecompressedLas);

        GeoprocessingPointCloudDecompressor.TryDecodeLasDataUri(uri, out var bytes).Should().BeTrue();
        bytes.Should().Equal(DecompressedLas);
    }

    [UnitTest]
    public void TryDecodeLasDataUri_RejectsNonLasOrMalformedUris()
    {
        GeoprocessingPointCloudDecompressor.TryDecodeLasDataUri(null, out _).Should().BeFalse();
        GeoprocessingPointCloudDecompressor.TryDecodeLasDataUri("https://x/y.las", out _).Should().BeFalse();
        GeoprocessingPointCloudDecompressor.TryDecodeLasDataUri("data:application/octet-stream;base64,QUJD", out _).Should().BeFalse();
        GeoprocessingPointCloudDecompressor.TryDecodeLasDataUri("data:application/vnd.las;base64,%%%", out _).Should().BeFalse();
        GeoprocessingPointCloudDecompressor.TryDecodeLasDataUri("data:application/vnd.las,nobase64", out _).Should().BeFalse();
    }

    private static string LasArtifact(byte[] payload)
        => $"data:application/vnd.las;base64,{Convert.ToBase64String(payload)}";

    private static GeoprocessingPointCloudDecompressor NewDecompressor(
        FakeGeoprocessingJobService job, TimeSpan? timeout = null)
    {
        var options = Options.Create(new PointCloudDecompressionOptions
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
            PollInterval = TimeSpan.FromMilliseconds(1),
        });
        return new GeoprocessingPointCloudDecompressor(
            job,
            new ClaimsPrincipal(new ClaimsIdentity(claims: [], authenticationType: "test")),
            options,
            NullLogger<GeoprocessingPointCloudDecompressor>.Instance);
    }

    private static Queue<ExecutionJobStatus> Statuses(params ExecutionJobStatus[] statuses)
        => new(statuses);
}
