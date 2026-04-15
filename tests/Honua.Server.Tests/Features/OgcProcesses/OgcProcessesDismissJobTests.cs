// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.OgcProcesses;

/// <summary>
/// Verifies that OGC job dismissal does not recreate a cancelled job from a stale
/// snapshot when the authoritative job record disappears between the initial read
/// and the post-notifier re-read.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.OgcApiProcesses)]
public sealed class OgcProcessesDismissJobTests : IAsyncLifetime
{
    private const string JobId = "geo-job-001";

    private readonly IExecutionJobStore _jobStore;
    private readonly IJobQueue _jobQueue;
    private readonly IUniversalProgressStore _progressStore;
    private readonly IJobCancellationNotifier _cancellationNotifier;
    private readonly WebAppFixture _fixture;

    public OgcProcessesDismissJobTests()
    {
        _jobStore = Substitute.For<IExecutionJobStore>();
        _jobQueue = Substitute.For<IJobQueue>();
        _progressStore = Substitute.For<IUniversalProgressStore>();
        _cancellationNotifier = Substitute.For<IJobCancellationNotifier>();

        var queuedJob = new ExecutionJobRecord
        {
            OperationId = JobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "test-backend",
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "geo-workload"
            }
        };

        _jobStore.GetAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(queuedJob, (ExecutionJobRecord?)null);
        _jobStore.GetAsync(Arg.Is<string>(id => id != JobId), Arg.Any<CancellationToken>())
            .Returns((ExecutionJobRecord?)null);
        _jobStore.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExecutionJobRecord>());

        _cancellationNotifier.Cancel(JobId).Returns(false);

        _fixture = new WebAppFixture()
            .ReplaceService(_jobStore)
            .ReplaceService(_jobQueue)
            .ReplaceService(_progressStore)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IJobCancellationNotifier>();
                services.AddSingleton(_cancellationNotifier);
            });
    }

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("DELETE /ogc/processes/jobs/{jobId}")]
    public async Task DismissJob_AlreadyCancelled_ReturnsOkWhenQueueRemovalFails()
    {
        var cancelledJob = new ExecutionJobRecord
        {
            OperationId = JobId,
            Status = ExecutionJobStatus.Cancelled,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "test-backend",
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "geo-workload"
            }
        };

        _jobStore.GetAsync(JobId, Arg.Any<CancellationToken>()).Returns(cancelledJob);
        _jobQueue.RemoveAsync(JobId, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Simulated Redis failure"));

        var response = await _fixture.Client.DeleteAsync($"/ogc/processes/jobs/{JobId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("DELETE /ogc/processes/jobs/{jobId}")]
    public async Task DismissJob_ReReadFindsDeleted_Returns404WithoutRecreatingJob()
    {
        var response = await _fixture.Client.DeleteAsync($"/ogc/processes/jobs/{JobId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("type").GetString().Should().Contain("no-such-job");

        await _jobStore.DidNotReceive().SetAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
        await _jobQueue.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _progressStore.DidNotReceive().SetProgressAsync(
            Arg.Any<string>(),
            Arg.Any<Honua.Core.Features.Infrastructure.Domain.IOperationProgress>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }
}
