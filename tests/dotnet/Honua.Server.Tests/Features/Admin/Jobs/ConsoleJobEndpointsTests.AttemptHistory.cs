// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/steps")]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/logs")]
    public async Task GetJobStepsAndLogs_ScopedReadKey_WithholdLostAttemptWorkerLikeDiagnostics()
    {
        // /steps, /logs and /diagnostics read the same log; the owner view must not serve on one
        // endpoint the worker identity it withholds on the other.
        const string AdminPassword = "console-job-steps-admin-bootstrap-key";
        var jobStore = new InMemoryJobStore();
        jobStore.Set(CreateJob("job-steps-owner", ExecutionJobStatus.Failed, DateTimeOffset.UtcNow.AddMinutes(-5), "corr-steps-owner"));
        var gatedFixture = GatedFixture(AdminPassword, jobStore, LostAttemptLogStore("job-steps-owner", "worker-internal-9"));
        await gatedFixture.InitializeAsync();
        try
        {
            using var admin = gatedFixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
            var readKey = await CreateApiKeyAsync(admin, "console-job-steps-read-only", ["admin:read"]);
            using var reader = gatedFixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", readKey.Key));

            var ownerStep = await ReadLostAttemptStepAsync(reader);
            ownerStep.GetProperty("metadata").TryGetProperty("worker.id", out _).Should().BeFalse();
            ownerStep.GetProperty("metadata").GetProperty("failure.classification").GetString().Should().Be("worker_lost");

            var ownerLogs = await reader.GetStringAsync("/api/v1/admin/jobs/job-steps-owner/logs");
            ownerLogs.Should().Contain("worker_lost").And.NotContain("worker-internal-9");

            var operatorStep = await ReadLostAttemptStepAsync(admin);
            operatorStep.GetProperty("metadata").GetProperty("worker.id").GetString().Should().Be("worker-internal-9");
            (await admin.GetStringAsync("/api/v1/admin/jobs/job-steps-owner/logs")).Should().Contain("worker-internal-9");
        }
        finally
        {
            await gatedFixture.DisposeAsync();
        }
    }

    private static WebAppFixture GatedFixture(string adminPassword, InMemoryJobStore jobStore, InMemoryLogStore logStore) => new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", adminPassword);
        })
        .ConfigureServices(services =>
        {
            services.RemoveAll<IExecutionJobStore>();
            services.RemoveAll<IExecutionLogStore>();
            services.AddSingleton<IExecutionJobStore>(jobStore);
            services.AddSingleton<IExecutionLogStore>(logStore);
        });

    private static async Task<JsonElement> ReadLostAttemptStepAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/admin/jobs/job-steps-owner/steps");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("steps").EnumerateArray()
            .Single(step => step.GetProperty("phase").GetString() == "worker-lost").Clone();
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
