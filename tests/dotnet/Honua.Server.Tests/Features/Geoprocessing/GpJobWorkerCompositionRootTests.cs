// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Regression coverage for honua-server#1841: the in-process job worker
/// (<c>AddJobWorker</c>) must be wired by the production composition root so that
/// authenticated GPServer <c>submitJob</c> jobs are claimed, executed, and advanced to a
/// terminal <c>esriJobSucceeded</c> status with a retrievable result package.
/// </summary>
/// <remarks>
/// The test supplies only a Redis connection string and a Pro dev grant as host settings;
/// the production composition root then wires the durable IConnectionMultiplexer, the job
/// store, the result-package store, the queue, AND — per the #1841 fix — the in-process
/// worker. Unlike <c>GPServerDurableRuntimeTests</c>, which calls <c>AddJobWorker()</c> in
/// its own <c>ConfigureServices</c> override to compensate for the missing wiring, this
/// test deliberately does NOT register the worker itself. That isolates the fix to a single
/// variable: the only thing that drains the queue is the worker the composition root now
/// registers. If it regresses to dead code, the worker assertion fails immediately and the
/// end-to-end poll would otherwise time out, reproducing the original bug.
/// </remarks>
[Collection("Redis")]
[Protocol(TestProtocols.GPServer)]
public sealed class GpJobWorkerCompositionRootTests(RedisFixture redis)
{
    private const string PointWkbBase64 = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string ServiceId = WebAppFixture.TestServiceId;
    private const string DurableArtifactUri = "https://example.test/composition-root-gp-output.geojson";

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/results/{paramName}")]
    public async Task SubmitJob_WithProductionWiredWorker_DrainsQueueToTerminalSuccess()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                // Set via UseSetting (host configuration) rather than ConfigureAppConfiguration
                // so these values are visible on builder.Configuration during Program's
                // pre-Build startup — that is when IsRedisCacheEntitledAsync runs and decides
                // whether to wire IConnectionMultiplexer. A deferred ConfigureAppConfiguration
                // source would be applied too late for that gate. The connection string + Pro
                // dev grant make the production composition root wire the multiplexer, the
                // durable job store/queue, and — per the #1841 fix — the in-process worker (it
                // self-gates on IJobQueue, which AddJobOrchestration only registers when the
                // multiplexer is present). This is the documented DevGrant=Pro topology from
                // #1827/#1787.
                builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
                builder.UseSetting("Licensing:DevGrantEdition", "Pro");
            })
            .ConfigureServices(services =>
            {
                // With the DevGrant=Pro + Redis host settings above, the production
                // composition root already wires the durable IConnectionMultiplexer, the job
                // store/result-package store/queue, AND (per the #1841 fix) the worker. The
                // only substitution this test makes is the executor: replace the production
                // geometry.buffer executor with a deterministic fixture so the test exercises
                // the worker wiring and the GPServer protocol projection independently of the
                // buffer implementation. The worker resolves IEnumerable<IJobExecutor> lazily
                // at host start, so this post-composition-root substitution is honored.
                //
                // NOTE: AddJobWorker() is intentionally NOT called here. The composition root
                // is solely responsible for it; if it regresses to dead code, the worker
                // assertion and the end-to-end poll below both fail.
                services.RemoveAll<IJobExecutor>();
                services.AddSingleton<IJobExecutor, SuccessfulGpServerJobExecutor>();
            });

        await fixture.InitializeAsync();
        try
        {
            // The in-process job worker must be registered by the production composition
            // root (FeatureRegistrationExtensions), not by this test. This is the direct
            // assertion of the #1841 fix; if AddJobWorker were dead code again, this fails
            // before the (slower) end-to-end poll below.
            var workerHostedServices = fixture.Services
                .GetServices<Microsoft.Extensions.Hosting.IHostedService>()
                .Select(h => h.GetType().Name)
                .ToArray();
            workerHostedServices.Should().Contain(
                "JobExecutionService",
                "the composition root must wire the GP job worker so queued jobs execute (#1841)");

            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["f"] = "json",
                ["wkb"] = PointWkbBase64,
                ["srid"] = "4326",
                ["distance"] = "25.5"
            });

            using var submit = await client.PostAsync(
                $"/rest/services/{ServiceId}/GPServer/geometry.buffer/submitJob",
                content);

            submit.StatusCode.Should().Be(HttpStatusCode.OK);
            using var submitDoc = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
            var jobId = submitDoc.RootElement.GetProperty("jobId").GetString();
            jobId.Should().NotBeNullOrWhiteSpace();
            submitDoc.RootElement.GetProperty("jobStatus").GetString().Should().Be("esriJobSubmitted");

            // The composition-root-wired worker must drain the queue to a terminal success.
            var terminalStatus = await PollUntilSucceededAsync(client, jobId!);
            var results = terminalStatus.RootElement.GetProperty("results");
            results.GetProperty("outputFeatureLayer").GetProperty("paramUrl").GetString()
                .Should().Be("results/outputFeatureLayer");

            using var result = await client.GetAsync(
                $"/rest/services/{ServiceId}/GPServer/geometry.buffer/jobs/{jobId}/results/outputFeatureLayer?f=json");

            result.StatusCode.Should().Be(HttpStatusCode.OK);
            using var resultDoc = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
            var resultRoot = resultDoc.RootElement;
            resultRoot.GetProperty("paramName").GetString().Should().Be("outputFeatureLayer");
            resultRoot.GetProperty("dataType").GetString().Should().Be("GPFeatureRecordSetLayer");
            resultRoot.GetProperty("value").GetString().Should().Be(DurableArtifactUri);

            var jobStore = fixture.GetService<IExecutionJobStore>();
            var durableJob = await jobStore.GetAsync(jobId!);
            durableJob.Should().NotBeNull();
            durableJob!.Status.Should().Be(ExecutionJobStatus.Succeeded);
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    private static async Task<JsonDocument> PollUntilSucceededAsync(HttpClient client, string jobId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync(
                $"/rest/services/{ServiceId}/GPServer/geometry.buffer/jobs/{jobId}?f=json");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.GetProperty("jobStatus").GetString();
            if (status == "esriJobSucceeded")
            {
                return JsonDocument.Parse(body);
            }

            status.Should().NotBe(
                "esriJobFailed",
                "the composition-root-wired worker should complete the bounded test job");
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException(
            $"Timed out waiting for GPServer job '{jobId}' to succeed. " +
            "This reproduces honua-server#1841: the in-process job worker was never wired, " +
            "so queued jobs are accepted but never drained.");
    }

    private static async Task DeleteControlPlaneKeysAsync(string redisConnectionString)
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
        var database = multiplexer.GetDatabase();
        var server = GetServer(multiplexer);
        var keys = server.Keys(pattern: "controlplane:*").ToArray();
        if (keys.Length > 0)
        {
            await database.KeyDeleteAsync(keys);
        }
    }

    private static IServer GetServer(ConnectionMultiplexer multiplexer)
    {
        var endpoints = multiplexer.GetEndPoints();
        if (endpoints.Length == 0)
        {
            throw new InvalidOperationException("Redis connection string did not provide any endpoints.");
        }

        return multiplexer.GetServer(endpoints[0]);
    }

    private sealed class SuccessfulGpServerJobExecutor : IJobExecutor
    {
        public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

        public async Task<JobExecutionResult> ExecuteAsync(
            ExecutionJobRecord job,
            IJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            await context.ReportProgressAsync(75, "Producing GPServer test output", cancellationToken);
            await context.PublishArtifactAsync(DurableArtifactUri, cancellationToken);
            return JobExecutionResult.Succeeded();
        }
    }
}
