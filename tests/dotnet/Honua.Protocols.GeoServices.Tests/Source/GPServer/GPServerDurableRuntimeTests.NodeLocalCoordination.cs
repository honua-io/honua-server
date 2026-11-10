// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

// The submission lease, the per-principal rate limit and the progress record each coordinate
// through Redis when a multiplexer is composed. These hosts have none, so each one runs on its
// per-node path and a job still has to be admitted, run and reported.
public sealed partial class GPServerDurableRuntimeTests
{
    private const int SubmissionsAllowedPerWindow = 2;
    private const int RateWindowSeconds = 3600;
    private const string BufferJobsUrl = $"/rest/services/{ServiceId}/GPServer/geometry.buffer";

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task SubmitJob_ConcurrentSubmissionsByOnePrincipalWithNoRedis_AreBothAdmittedAndComplete()
    {
        var fixture = CreateDurableFixture(new SuccessfulGpServerJobExecutor());
        await NoRedisJobHost.StartAsync(fixture);
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = GPServerJobWait.RequestTimeout;

            var submissions = await Task.WhenAll(SubmitBufferJobAsync(client), SubmitBufferJobAsync(client));

            var jobIds = submissions.Select(submission => submission.AdmittedJobId()).ToArray();
            jobIds.Should().OnlyHaveUniqueItems();
            var jobStore = fixture.GetService<IExecutionJobStore>();
            foreach (var jobId in jobIds)
            {
                using var terminal = await GPServerJobWait.UntilRestSucceededAsync(
                    client, $"{BufferJobsUrl}/jobs/{jobId}?f=json", jobId, jobStore);
                (await jobStore.GetAsync(jobId))!.Status.Should().Be(ExecutionJobStatus.Succeeded);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task SubmitJob_OverThePerPrincipalRateLimitWithNoRedis_IsRefusedAndAdmittedJobsComplete()
    {
        var fixture = CreateDurableFixture(new SuccessfulGpServerJobExecutor())
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("ExecutionAdmission:MaxSubmissionsPerWindow", $"{SubmissionsAllowedPerWindow}");
                builder.UseSetting("ExecutionAdmission:RateWindowSeconds", $"{RateWindowSeconds}");
            });
        await NoRedisJobHost.StartAsync(fixture);
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = GPServerJobWait.RequestTimeout;

            var admittedJobIds = new List<string>();
            while (admittedJobIds.Count < SubmissionsAllowedPerWindow)
            {
                admittedJobIds.Add((await SubmitBufferJobAsync(client)).AdmittedJobId());
            }

            var refused = await SubmitBufferJobAsync(client);

            refused.Body.TryGetProperty("jobId", out _).Should().BeFalse(refused.Body.GetRawText());
            var error = refused.Body.GetProperty("error");
            error.GetProperty("code").GetInt32().Should().Be((int)HttpStatusCode.ServiceUnavailable);
            var rateLimitReason =
                $"Principal submission rate limit reached ({SubmissionsAllowedPerWindow}/{SubmissionsAllowedPerWindow} in {RateWindowSeconds}s).";
            error.GetProperty("details").EnumerateArray().Select(detail => detail.GetString())
                .Should().Contain(detail => detail != null && detail.Contains(rateLimitReason, StringComparison.Ordinal));
            error.GetProperty("retryable").GetBoolean().Should().BeTrue();
            var retryAfterSeconds = error.GetProperty("retryAfterSeconds").GetInt32();
            retryAfterSeconds.Should().BePositive();
            refused.RetryAfter.Should().Be($"{retryAfterSeconds}");

            var jobStore = fixture.GetService<IExecutionJobStore>();
            foreach (var jobId in admittedJobIds)
            {
                using var terminal = await GPServerJobWait.UntilRestSucceededAsync(
                    client, $"{BufferJobsUrl}/jobs/{jobId}?f=json", jobId, jobStore);
            }

            (await jobStore.QueryAsync(new ExecutionJobQuery())).Items
                .Where(job => job.Spec.Kind == ExecutionJobKind.Geoprocessing)
                .Select(job => job.OperationId)
                .Should().BeEquivalentTo(admittedJobIds);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task JobStatus_WhileTheExecutorHoldsAJobWithNoRedis_ReportsItsProgressAndTheProgressRecordFollowsTheJob()
    {
        var executor = new HeldGpServerJobExecutor();
        var fixture = CreateDurableFixture(executor);
        await NoRedisJobHost.StartAsync(fixture);
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = GPServerJobWait.RequestTimeout;
            var jobId = (await SubmitBufferJobAsync(client)).AdmittedJobId();
            var statusUrl = $"{BufferJobsUrl}/jobs/{jobId}?f=json";
            var progressStore = fixture.GetService<IUniversalProgressStore>();

            await executor.ProgressReported.WaitAsync(GPServerJobWait.JobBudget);

            using var statusResponse = await client.GetAsync(statusUrl);
            statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var running = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
            running.RootElement.GetProperty("jobStatus").GetString().Should().Be("esriJobExecuting");
            running.RootElement.GetProperty("messages").EnumerateArray()
                .Select(message => message.GetProperty("description").GetString())
                .Should().Contain(HeldGpServerJobExecutor.ReportedPhase);
            var progressWhileRunning = await progressStore.GetProgressAsync<GeoprocessingProgress>(jobId);
            progressWhileRunning.Should().NotBeNull();
            progressWhileRunning!.Status.Should().BeOneOf(OperationStatus.Queued, OperationStatus.Processing);

            executor.Release();

            using var terminal = await GPServerJobWait.UntilRestSucceededAsync(
                client, statusUrl, jobId, fixture.GetService<IExecutionJobStore>());
            var completedProgress = await WaitForProgressStatusAsync(progressStore, jobId, OperationStatus.Completed);
            completedProgress.CompletedAt.Should().NotBeNull();
        }
        finally
        {
            executor.Release();
            await fixture.DisposeAsync();
        }
    }

    private static async Task<BufferSubmission> SubmitBufferJobAsync(HttpClient client)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["wkb"] = PointWkbBase64,
            ["srid"] = "4326",
            ["distance"] = "25.5"
        });
        using var response = await client.PostAsync($"{BufferJobsUrl}/submitJob", content);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var retryAfter = response.Headers.TryGetValues("Retry-After", out var values) ? values.Single() : null;
        return new BufferSubmission(body.RootElement.Clone(), retryAfter);
    }

    private static async Task<GeoprocessingProgress> WaitForProgressStatusAsync(
        IUniversalProgressStore progressStore,
        string jobId,
        OperationStatus expectedStatus)
    {
        var elapsed = Stopwatch.StartNew();
        GeoprocessingProgress? lastObserved = null;
        while (elapsed.Elapsed < GPServerJobWait.JobBudget)
        {
            lastObserved = await progressStore.GetProgressAsync<GeoprocessingProgress>(jobId);
            if (lastObserved?.Status == expectedStatus)
            {
                return lastObserved;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException(
            $"Progress for job '{jobId}' did not reach {expectedStatus}. Last observed: {lastObserved?.Status.ToString() ?? "none"}.");
    }

    private sealed record BufferSubmission(JsonElement Body, string? RetryAfter)
    {
        public string AdmittedJobId()
        {
            Body.TryGetProperty("jobId", out var jobId).Should().BeTrue(Body.GetRawText());
            return jobId.GetString()!;
        }
    }

    private sealed class HeldGpServerJobExecutor : IJobExecutor
    {
        public const string ReportedPhase = "Holding GPServer test output";

        private readonly TaskCompletionSource _progressReported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

        public Task ProgressReported => _progressReported.Task;

        public void Release() => _released.TrySetResult();

        public async Task<JobExecutionResult> ExecuteAsync(
            ExecutionJobRecord job,
            IJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            await context.ReportProgressAsync(40, ReportedPhase, cancellationToken);
            _progressReported.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
            await context.PublishArtifactAsync("https://example.test/durable-gp-output.geojson", cancellationToken);
            return JobExecutionResult.Succeeded();
        }
    }
}
