// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Extensions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.Server.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Geoprocessing;

[Collection(WebAppFixtureJobSubstrateExtensions.JobSubstrateCollection)]
[Protocol(TestProtocols.GPServer)]
public sealed class GpJobWorkerCompositionRootTests(DatabaseFixtureAdapter database)
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
        await database.DataSource.ClearJobSubstrateAsync();

        var fixture = new WebAppFixture().WithProductionJobSubstrate()
            .ConfigureWebHost(builder =>
            {
                // Host configuration is visible to Program's pre-Build startup.
                builder.UseSetting("Licensing:DevGrantEdition", "Pro");
            })
            .ConfigureServices(services =>
            {
                // The production composition root wires the PostgreSQL job store,
                // result-package store, queue and worker. The only substitution this test
                // makes is the executor: replace the production
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
            // assertion of that wiring; if AddJobWorker were dead code, this fails
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
            // The production worker can advance the durable job before submission returns.
            submitDoc.RootElement.GetProperty("jobStatus").GetString().Should().BeOneOf(
                "esriJobSubmitted", "esriJobWaiting", "esriJobExecuting", "esriJobSucceeded");

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
            // Esri GPFeatureRecordSetLayer URL results use an object containing url:
            // https://developers.arcgis.com/rest/services-reference/enterprise/gp-data-types/#feature-service-output
            var value = resultRoot.GetProperty("value");
            value.ValueKind.Should().Be(JsonValueKind.Object);
            value.EnumerateObject().Should().ContainSingle().Which.Name.Should().Be("url");
            value.GetProperty("url").GetString().Should().Be(DurableArtifactUri);

            var jobStore = fixture.GetService<IExecutionJobStore>();
            var durableJob = await jobStore.GetAsync(jobId!);
            durableJob.Should().NotBeNull();
            durableJob!.Status.Should().Be(ExecutionJobStatus.Succeeded);
        }
        finally
        {
            await fixture.DisposeAsync();
            await database.DataSource.ClearJobSubstrateAsync();
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
