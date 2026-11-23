// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using NSubstitute;
using InfrastructureStatus = Honua.Core.Features.Infrastructure.Domain.OperationStatus;

namespace Honua.Server.Tests.Features.OperationsToolset;

// A large layer publish answers 202 with a background job. The executor must report it Queued
// with the job id, so an approved proposal reads Submitted and then the job's terminal state
// instead of Succeeded with nothing to follow.
public sealed partial class AdminOperationRequestContractTests
{
    private const string QueuedJobId = "job-170a8f64";
    private static readonly DateTimeOffset JobFinishedAt = new(2026, 11, 23, 4, 30, 0, TimeSpan.Zero);

    [UnitTest]
    public async Task SubmitAsync_QueuedLayerPublish_ReportsQueuedHandleWithJobId()
    {
        var jobs = Substitute.For<ILayerPublishJobs>();
        using var capture = new CaptureHandler(AcceptedLayerPublishBody(), HttpStatusCode.Accepted);
        using var client = new HttpClient(capture);
        var executor = CreateLayerPublishApi(client, jobs);

        var handle = await SubmitLayerPublishAsync(executor);

        handle.Status.Should().Be(OperationHandleStatus.Queued);
        handle.JobId.Should().Be(QueuedJobId);
        handle.ResourceIds.Should().BeEmpty();
        handle.Result!.Details["jobId"].Should().Be(QueuedJobId);
        handle.Result.Details["statusUrl"].Should().Be($"/api/v1/admin/jobs/{QueuedJobId}");
        handle.Result.Details["rowsTotal"].Should().Be("2000000");
    }

    [UnitTheory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"success\":true,\"data\":{\"storageLayerId\":42,\"serviceName\":\"roads\"}}")]
    public async Task SubmitAsync_AcceptedLayerPublishWithoutJob_ReportsIndeterminateNotCompleted(string payload)
    {
        using var capture = new CaptureHandler(payload, HttpStatusCode.Accepted);
        using var client = new HttpClient(capture);
        var executor = CreateApi("admin.layer.publish", client);

        var handle = await SubmitLayerPublishAsync(executor);

        handle.Status.Should().Be(OperationHandleStatus.Indeterminate);
        handle.Reason.Should().Be("Queued layer publish did not return a job id.");
        handle.Result!.Summary.Should().NotContain("completed");
        handle.JobId.Should().BeNull();
        handle.ResourceIds.Should().BeEmpty();
    }

    [UnitTest]
    public async Task GetStatusAsync_FailedLayerPublishJob_ReportsFailureWithJobReason()
    {
        var jobs = Substitute.For<ILayerPublishJobs>();
        jobs.GetAsync(QueuedJobId, Arg.Any<CancellationToken>()).Returns(Progress(InfrastructureStatus.Failed) with
        {
            ErrorMessage = "Layer publish exceeded its 1-second budget.",
        });
        using var capture = new CaptureHandler(AcceptedLayerPublishBody(), HttpStatusCode.Accepted);
        using var client = new HttpClient(capture);
        var executor = CreateLayerPublishApi(client, jobs);

        var status = await executor.GetStatusAsync(await SubmitLayerPublishAsync(executor));

        status.Status.Should().Be(OperationHandleStatus.Failed);
        status.Reason.Should().Be("Layer publish exceeded its 1-second budget.");
        status.UpdatedAt.Should().Be(JobFinishedAt);
        status.ResourceIds.Should().BeEmpty();
    }

    [UnitTest]
    public async Task GetStatusAsync_CompletedLayerPublishJob_ReportsCommittedLayer()
    {
        var jobs = Substitute.For<ILayerPublishJobs>();
        jobs.GetAsync(QueuedJobId, Arg.Any<CancellationToken>())
            .Returns(Progress(InfrastructureStatus.Completed) with { LayerId = 42 });
        using var capture = new CaptureHandler(AcceptedLayerPublishBody(), HttpStatusCode.Accepted);
        using var client = new HttpClient(capture);
        var executor = CreateLayerPublishApi(client, jobs);

        var status = await executor.GetStatusAsync(await SubmitLayerPublishAsync(executor));

        status.Status.Should().Be(OperationHandleStatus.Completed);
        status.ResourceIds.Should().Contain("storageLayerId", "42");
        status.Result!.Details["jobId"].Should().Be(QueuedJobId);
    }

    [UnitTest]
    public async Task GetStatusAsync_UnknownLayerPublishJob_KeepsQueuedHandle()
    {
        var jobs = Substitute.For<ILayerPublishJobs>();
        jobs.GetAsync(QueuedJobId, Arg.Any<CancellationToken>()).Returns((LayerPublishJobProgress?)null);
        using var capture = new CaptureHandler(AcceptedLayerPublishBody(), HttpStatusCode.Accepted);
        using var client = new HttpClient(capture);
        var executor = CreateLayerPublishApi(client, jobs);

        var status = await executor.GetStatusAsync(await SubmitLayerPublishAsync(executor));

        status.Status.Should().Be(OperationHandleStatus.Queued);
    }

    private static Task<OperationHandle> SubmitLayerPublishAsync(AdminApiOperationExecutor executor)
        => executor.SubmitAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            ConnectionId = "11111111-1111-1111-1111-111111111111",
            ServiceName = "requested-service",
        }, new OperationPolicyContext());

    private static string AcceptedLayerPublishBody() => JsonSerializer.Serialize(
        ApiResponse<LayerPublishAcceptedResponse>.CreateSuccess(new LayerPublishAcceptedResponse
        {
            JobId = QueuedJobId,
            StatusUrl = $"/api/v1/admin/jobs/{QueuedJobId}",
            RowsTotal = 2_000_000,
            Status = "Queued",
        }),
        LayerPublishingJsonContext.Default.ApiResponseLayerPublishAcceptedResponse);

    private static AdminApiOperationExecutor CreateLayerPublishApi(HttpClient client, ILayerPublishJobs jobs)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AdminApiOperationExecutor.HttpClientName).Returns(client);
        return new AdminApiOperationExecutor(
            AdminApiOperationCatalog.Definitions.Single(definition => definition.OperationId == "admin.layer.publish"),
            factory, Context(), new InMemoryAdminApiKeyStore(TimeProvider.System), TimeProvider.System,
            new OperationLineageAttestationStore(TimeProvider.System))
        {
            LayerPublishJobs = jobs,
        };
    }

    private static LayerPublishJobProgress Progress(InfrastructureStatus status) => new()
    {
        OperationId = QueuedJobId,
        Status = status,
        RowsTotal = 2_000_000,
        StartedAt = JobFinishedAt.AddMinutes(-1),
        CompletedAt = JobFinishedAt,
    };
}
