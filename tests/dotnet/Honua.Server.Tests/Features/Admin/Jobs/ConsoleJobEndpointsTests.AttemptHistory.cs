// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin.Jobs;

// Jobs retried after a lost worker: one classification on both endpoints, no attempt past the
// budget, and no cross-attempt span reported as a measured queue delay.
public sealed partial class ConsoleJobEndpointsTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}")]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/diagnostics")]
    public async Task WorkerLostJob_DetailAndDiagnostics_AgreeOnClassificationAndRetry()
    {
        var now = DateTimeOffset.UtcNow;
        _jobStore.Set(CreateJob("job-worker-lost", ExecutionJobStatus.Failed, now.AddMinutes(-20), "corr-worker-lost") with
        {
            ClaimedAt = now.AddMinutes(-4),
            CompletedAt = now.AddMinutes(-1),
            CurrentPhase = "Failed (heartbeat expired)",
            ErrorMessage = "Worker heartbeat expired after 3 attempt(s).",
            AttemptCount = 3,
            RetryPolicy = new JobRetryPolicy { MaxAttempts = 3 }
        });

        using var detail = await ReadJsonAsync(await _client.GetAsync("/api/v1/admin/jobs/job-worker-lost"));
        detail.RootElement.GetProperty("failure").GetProperty("classification").GetString().Should().Be("worker_lost");

        var response = await _client.GetAsync("/api/v1/admin/jobs/job-worker-lost/diagnostics");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var diagnostics = await ReadJsonAsync(response);
        var root = diagnostics.RootElement;
        root.GetProperty("failure").GetProperty("classification").GetString().Should().Be("worker_lost");
        var retry = root.GetProperty("attempt").GetProperty("retry");
        retry.GetProperty("allowed").GetBoolean().Should().BeFalse();
        retry.GetProperty("disabledReason").GetString().Should().Be("retry budget exhausted");
        retry.GetProperty("wouldRepeat").GetString().Should()
            .StartWith("nothing is re-dispatched: all 3 attempts allowed by the retry policy have been used")
            .And.NotContain("attempt 4");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/diagnostics")]
    public async Task RecoveredJob_Diagnostics_ShowsLostAttemptAndDoesNotMeasureQueueDelayAcrossAttempts()
    {
        var now = DateTimeOffset.UtcNow;
        _jobStore.Set(CreateJob("job-recovered", ExecutionJobStatus.Succeeded, now.AddMinutes(-6), "corr-recovered") with
        {
            ClaimedAt = now.AddMinutes(-2),
            CompletedAt = now.AddMinutes(-1),
            AttemptCount = 2,
            RetryPolicy = new JobRetryPolicy { MaxAttempts = 3 }
        });
        _logStore.SetLogs("job-recovered", LostAttemptEvent(now.AddMinutes(-3), "worker-gone"));

        using var diagnostics = await ReadJsonAsync(await _client.GetAsync("/api/v1/admin/jobs/job-recovered/diagnostics"));
        var root = diagnostics.RootElement;
        var lifecycle = root.GetProperty("lifecycle");
        lifecycle.TryGetProperty("queueDelayMs", out _).Should().BeFalse("a queue delay is not measured across attempts");
        lifecycle.GetProperty("executionMs").GetInt64().Should().BeInRange(59_000, 61_000);
        root.GetProperty("evidence").GetProperty("missing").EnumerateArray().Select(m => m.GetString())
            .Should().Contain(m => m!.StartsWith("lifecycle.queueDelayMs: attempt 2", StringComparison.Ordinal));

        var lostEvent = root.GetProperty("events").GetProperty("items").EnumerateArray().Single();
        lostEvent.GetProperty("phase").GetString().Should().Be("worker-lost");
        lostEvent.GetProperty("metadata").GetProperty("worker.id").GetString().Should().Be("worker-gone");
    }

    // The reconciler's record of a lost attempt, as stored in the job log.
    private static ExecutionLogEntry LostAttemptEvent(DateTimeOffset endedAt, string workerId) => new()
    {
        Timestamp = endedAt,
        Level = ExecutionLogLevel.Warning,
        Message = "Attempt 1 of 3 lost: the worker stopped heartbeating",
        Phase = "worker-lost",
        Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["attempt"] = "1",
            ["worker.id"] = workerId,
            ["failure.classification"] = "worker_lost"
        }
    };

    private static InMemoryLogStore LostAttemptLogStore(string operationId, string workerId)
    {
        var store = new InMemoryLogStore();
        store.SetLogs(operationId, LostAttemptEvent(DateTimeOffset.UtcNow.AddMinutes(-1), workerId));
        return store;
    }
}
