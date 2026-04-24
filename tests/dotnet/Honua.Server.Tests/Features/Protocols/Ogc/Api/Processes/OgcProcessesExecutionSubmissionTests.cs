// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Tests.Helpers;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes;

[Collection("Database")]
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
