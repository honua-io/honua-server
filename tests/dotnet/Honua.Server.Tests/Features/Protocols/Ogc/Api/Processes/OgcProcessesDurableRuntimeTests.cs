// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.ControlPlane;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes;

/// <summary>
/// Verifies first-slice OGC API Processes execution against the durable runtime.
/// </summary>
[Collection("Redis")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class OgcProcessesDurableRuntimeTests(RedisFixture redis)
{
    private const string PointWkbBase64 = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA";

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task Execute_FirstSliceVectorProcess_CompletesAndReturnsDurableResultEvidence()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:redis"] = redis.ConnectionString
                    });
                });
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IConnectionMultiplexer>();
                services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis.ConnectionString));

                services.RemoveAll<IExecutionJobStore>();
                services.AddSingleton<IExecutionJobStore>(sp =>
                    new RedisExecutionJobStore(
                        sp.GetRequiredService<IConnectionMultiplexer>(),
                        sp.GetRequiredService<ILogger<RedisExecutionJobStore>>()));

                services.RemoveAll<IGeoprocessingResultPackageStore>();
                services.AddSingleton<IGeoprocessingResultPackageStore>(sp =>
                    new RedisGeoprocessingResultPackageStore(
                        sp.GetRequiredService<IConnectionMultiplexer>(),
                        sp.GetRequiredService<ILogger<RedisGeoprocessingResultPackageStore>>()));

                services.RemoveAll<RedisJobQueue>();
                services.RemoveAll<IJobQueue>();
                services.RemoveAll<IQueueClaimReconciler>();
                services.AddSingleton<RedisJobQueue>(sp =>
                    new RedisJobQueue(
                        sp.GetRequiredService<IConnectionMultiplexer>(),
                        sp.GetRequiredService<IExecutionJobStore>(),
                        sp.GetRequiredService<ILogger<RedisJobQueue>>()));
                services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<RedisJobQueue>());
                services.AddSingleton<IQueueClaimReconciler>(sp => sp.GetRequiredService<RedisJobQueue>());

                services.RemoveAll<IExecutionLogStore>();
                services.AddSingleton<IExecutionLogStore>(sp =>
                    new RedisExecutionLogStore(
                        sp.GetRequiredService<IConnectionMultiplexer>(),
                        sp.GetRequiredService<ILogger<RedisExecutionLogStore>>()));

                // The production geometry.buffer executor is registered by
                // AddGeoprocessing; this test exercises the durable-runtime
                // protocol projection independently of the buffer implementation,
                // so swap in a deterministic fixture executor instead.
                services.RemoveAll<IJobExecutor>();
                services.AddSingleton<IJobExecutor, SuccessfulOgcVectorProcessExecutor>();
                services.AddJobWorker();
            });

        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "/ogc/processes/processes/geometry.buffer/execution");
            request.Headers.Add("Prefer", "respond-async");
            request.Content = new StringContent(
                "{\"response\":\"document\",\"inputs\":{\"wkb\":\"" +
                PointWkbBase64 +
                "\",\"srid\":4326,\"distance\":25.5}}",
                Encoding.UTF8,
                "application/json");

            using var submit = await client.SendAsync(request);
            submit.StatusCode.Should().Be(HttpStatusCode.Created);
            submit.Headers.Location.Should().NotBeNull();
            submit.Headers.TryGetValues("Preference-Applied", out var applied).Should().BeTrue();
            applied.Should().Contain("respond-async");

            using var submitDoc = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
            var jobId = submitDoc.RootElement.GetProperty("jobID").GetString();
            jobId.Should().NotBeNullOrWhiteSpace();

            using var terminalStatus = await PollUntilSucceededAsync(client, jobId!);
            terminalStatus.RootElement.GetProperty("links").EnumerateArray().Should().Contain(link =>
                link.GetProperty("rel").GetString() == "http://www.opengis.net/def/rel/ogc/1.0/results" &&
                link.GetProperty("href").GetString()!.EndsWith(
                    $"/ogc/processes/jobs/{jobId}/results",
                    StringComparison.Ordinal));

            using var result = await client.GetAsync($"/ogc/processes/jobs/{jobId}/results");
            result.StatusCode.Should().Be(HttpStatusCode.OK);
            using var resultDoc = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
            var output = resultDoc.RootElement.GetProperty("outputFeatureLayer");
            output.GetProperty("id").GetString().Should().Be($"{jobId}:artifact:1");
            output.GetProperty("kind").GetString().Should().Be("FeatureLayer");
            output.GetProperty("href").GetString().Should().Be("https://example.test/ogc-buffer-output.geojson");
            output.GetProperty("type").GetString().Should().Be("application/geo+json");

            var jobStore = fixture.GetService<IExecutionJobStore>();
            var durableJob = await jobStore.GetAsync(jobId!);
            durableJob.Should().NotBeNull();
            durableJob!.Status.Should().Be(ExecutionJobStatus.Succeeded);
            durableJob.Spec.Parameters.Should().Contain(
                new KeyValuePair<string, string>("submittedVia", "OGC-API-Processes"));
            durableJob.Spec.Parameters.Should().Contain(
                new KeyValuePair<string, string>("protocolProcessId", "geometry.buffer"));
            durableJob.Spec.Parameters.Should().Contain(
                new KeyValuePair<string, string>("process.output.0", "outputFeatureLayer"));

            var resultStore = fixture.GetService<IGeoprocessingResultPackageStore>();
            var package = await resultStore.GetAsync(jobId!);
            package.Should().NotBeNull();
            package!.Artifacts.Should().ContainSingle(artifact =>
                artifact.Kind == ArtifactKind.FeatureLayer &&
                artifact.Label == "outputFeatureLayer" &&
                artifact.Uri == "https://example.test/ogc-buffer-output.geojson");
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
            using var response = await client.GetAsync($"/ogc/processes/jobs/{jobId}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.GetProperty("status").GetString();
            if (status == "successful")
            {
                return JsonDocument.Parse(body);
            }

            status.Should().NotBe("failed", "the configured durable runtime should complete the bounded test job");
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException($"Timed out waiting for OGC process job '{jobId}' to succeed.");
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

    private sealed class SuccessfulOgcVectorProcessExecutor : IJobExecutor
    {
        public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

        public async Task<JobExecutionResult> ExecuteAsync(
            ExecutionJobRecord job,
            IJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            await context.ReportProgressAsync(75, "Producing OGC vector process test output", cancellationToken);
            await context.PublishArtifactAsync("https://example.test/ogc-buffer-output.geojson", cancellationToken);
            return JobExecutionResult.Succeeded();
        }
    }
}
