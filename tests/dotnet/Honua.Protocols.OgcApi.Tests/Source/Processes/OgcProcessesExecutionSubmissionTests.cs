// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.TestKit.Helpers;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes;

[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class OgcProcessesExecutionSubmissionTests : IAsyncLifetime
{
    private readonly IExecutionJobStore _jobStore;
    private readonly IJobQueue _jobQueue;
    private readonly IUniversalProgressStore _progressStore;
    private readonly WebAppFixture _fixture;

    public OgcProcessesExecutionSubmissionTests()
    {
        _jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        _jobQueue = Substitute.For<IJobQueue>();
        _progressStore = Substitute.For<IUniversalProgressStore>();

        ExecutionJobRecord? createdJob = null;
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                createdJob = call.Arg<ExecutionJobRecord>();
                return true;
            });
        _jobStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => createdJob == null ? null : createdJob with { Version = 1 });
        _jobQueue.EnqueueAsync(Arg.Any<string>(), Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Redis unavailable"));

        _fixture = new WebAppFixture()
            .ReplaceService(_jobStore)
            .ReplaceService(_jobQueue)
            .ReplaceService(_progressStore);
    }

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_WhenQueuePersistenceFails_RollsBackCreatedJobUsingFreshVersionToken()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        request.Headers.Add("Prefer", "respond-async");
        // Plan must pass catalog validation so the code path reaches enqueue
        // and exercises the rollback branch. geometry.buffer requires wkb,
        // srid, and distance.
        request.Content = new StringContent(
            """{"inputs":{"plan":{"planId":"plan-rollback","steps":[{"stepId":"s1","kind":"geoprocess","processId":"geometry.buffer","inputs":{"wkb":"AAAA","srid":"4326","distance":"100"}}]}}}""",
            Encoding.UTF8,
            "application/json");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        await _jobStore.Received().TrySetAsync(
            Arg.Is<ExecutionJobRecord>(j =>
                j.Status == ExecutionJobStatus.Failed &&
                j.Version == 1 &&
                j.CurrentPhase == "Failed (submission)"),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_RasterProcessDirect_CreatesSameJobAsPlanWrapped()
    {
        // #2698 parity proof: a direct raster/surface process-id execution must reach
        // the shared submission pipeline and create the SAME canonical job that the
        // honua-geoprocessing single-step plan wrapper creates — not a new synchronous
        // in-process call. Both paths run through the real GeoprocessingJobService
        // (tier/RBAC/approval/admission gates included), so we capture the durable
        // ExecutionJobRecord.Spec each path builds and assert they encode surface.slope
        // identically.
        var created = new List<ExecutionJobRecord>();
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                created.Add(call.Arg<ExecutionJobRecord>());
                return true;
            });

        // Direct process-id execution: inputs are the process parameters directly.
        using var directRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/ogc/processes/processes/surface.slope/execution");
        directRequest.Headers.Add("Prefer", "respond-async");
        directRequest.Content = new StringContent(
            """{"inputs":{"source":"AAAA","units":"degrees"}}""",
            Encoding.UTF8,
            "application/json");
        await _fixture.Client.SendAsync(directRequest);

        // Plan-wrapped execution: a single-step geoprocess plan wrapping surface.slope,
        // the only way to invoke this tool before #2698.
        using var wrappedRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        wrappedRequest.Headers.Add("Prefer", "respond-async");
        wrappedRequest.Content = new StringContent(
            """{"inputs":{"plan":{"planId":"pw-surface-slope","steps":[{"stepId":"s1","kind":"geoprocess","processId":"surface.slope","inputs":{"source":"AAAA","units":"degrees"}}],"outputs":["raster"]}}}""",
            Encoding.UTF8,
            "application/json");
        await _fixture.Client.SendAsync(wrappedRequest);

        // Both paths reached job creation (not 404 no-such-process).
        created.Should().HaveCount(2, "both the direct and plan-wrapped paths must create a job");

        var directSpec = created[0].Spec;
        var wrappedSpec = created[1].Spec;

        // The durable spec encodes exactly the surface.slope process, its inputs, and
        // its raster output on BOTH paths — the same job, submitted asynchronously.
        directSpec.Parameters.Values.Should().Contain("surface.slope");
        wrappedSpec.Parameters.Values.Should().Contain("surface.slope");

        // Step inputs (source + units) project onto identical keys/values on both paths.
        var directInputs = directSpec.Parameters
            .Where(kv => kv.Key.EndsWith(".source", StringComparison.Ordinal)
                || kv.Key.EndsWith(".units", StringComparison.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToArray();
        var wrappedInputs = wrappedSpec.Parameters
            .Where(kv => kv.Key.EndsWith(".source", StringComparison.Ordinal)
                || kv.Key.EndsWith(".units", StringComparison.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToArray();
        directInputs.Should().NotBeEmpty();
        directInputs.Should().BeEquivalentTo(wrappedInputs);

        // The runtime profile that routes to the GDAL worker is identical on both paths.
        directSpec.RuntimeProfile.Should().Be(wrappedSpec.RuntimeProfile);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_WhenCreateFails_DoesNotReturnCreated()
    {
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _jobStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ExecutionJobRecord?)null);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        request.Content = new StringContent(
            """{"inputs":{"plan":{"planId":"plan-create-fail","steps":[{"stepId":"s1","kind":"queryFeatures"}]}}}""",
            Encoding.UTF8,
            "application/json");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        await _jobQueue.DidNotReceive().EnqueueAsync(
            Arg.Any<string>(),
            Arg.Any<OperationPriority>(),
            Arg.Any<CancellationToken>());
    }
}
