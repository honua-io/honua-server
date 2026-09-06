// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Geoprocessing;
using Honua.ControlPlane;
using Honua.Server.Features.Geoprocessing;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>
/// End-to-end proof for honua-server#2349: the default/seeded GPServer service
/// (<see cref="GeoprocessingServiceSeeder.ServiceName"/>) can be driven to a terminal
/// job with results through the GPServer facade using the real geometry.buffer
/// executor and the durable Redis runtime — i.e. GPServer is demonstrably usable out
/// of the box once the default service is present. The seeder that materializes a
/// service of exactly this shape is unit-tested in
/// <c>GeoprocessingServiceSeederTests</c>.
/// </summary>
[Collection("Redis")]
[Protocol(TestProtocols.GPServer)]
public sealed class GPServerDefaultServiceTests(RedisFixture redis)
{
    // POINT(0 0) as base64 WKB — the same deterministic input the durable runtime
    // test uses for geometry.buffer.
    private const string PointWkbBase64 = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string ServiceId = GeoprocessingServiceSeeder.ServiceName;

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/results/{paramName}")]
    public async Task DefaultGpService_DrivesGeometryBufferToTerminalResult()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        // A graph containing a service of exactly the seeded shape (name, GPServer
        // protocol, anonymous access) and nothing else — this is what a fresh
        // instance looks like after the default GP service seed runs.
        var graph = new TestMetadataV2GraphBuilder()
            .AddService(
                GeoprocessingServiceSeeder.ServiceId,
                GeoprocessingServiceSeeder.ServiceName,
                route: $"/rest/services/{GeoprocessingServiceSeeder.ServiceName}/GPServer",
                protocols: [MetadataV2ServiceProtocols.GPServer],
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .Build();
        var graphProvider = new TestMetadataV2GraphProvider(graph);

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
                // Use the seeded-shape GP service graph.
                services.RemoveAll<IMetadataV2GraphProvider>();
                services.RemoveAll<IMetadataV2GraphStore>();
                services.AddSingleton<IMetadataV2GraphProvider>(graphProvider);
                services.AddSingleton<IMetadataV2GraphStore>(graphProvider);

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
                        sp.GetRequiredService<IOptionsMonitor<GeoprocessingExecutorOptions>>(),
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

                // Keep the REAL geometry.buffer executor (registered by AddGeoprocessing)
                // and run the worker so the seeded service is driven to a genuine
                // terminal result, not a fixture stub.
                services.AddJobWorker();
            });

        await fixture.InitializeAsync();
        try
        {
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

            submit.StatusCode.Should().Be(HttpStatusCode.OK,
                "the seeded default GP service must resolve through the GPServer facade out of the box");
            using var submitDoc = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
            var jobId = submitDoc.RootElement.GetProperty("jobId").GetString();
            jobId.Should().NotBeNullOrWhiteSpace();
            submitDoc.RootElement.GetProperty("jobStatus").GetString().Should().Be("esriJobSubmitted");

            var terminal = await PollUntilSucceededAsync(client, jobId!);
            terminal.RootElement.GetProperty("results").GetProperty("outputFeatureLayer")
                .GetProperty("paramUrl").GetString()
                .Should().Be("results/outputFeatureLayer");

            using var result = await client.GetAsync(
                $"/rest/services/{ServiceId}/GPServer/geometry.buffer/jobs/{jobId}/results/outputFeatureLayer?f=json");

            result.StatusCode.Should().Be(HttpStatusCode.OK);
            using var resultDoc = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
            var resultRoot = resultDoc.RootElement;
            resultRoot.GetProperty("paramName").GetString().Should().Be("outputFeatureLayer");
            resultRoot.GetProperty("dataType").GetString().Should().Be("GPFeatureRecordSetLayer");
            resultRoot.GetProperty("value").GetString().Should().StartWith("data:application/geo+json;base64,");
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

            status.Should().NotBe("esriJobFailed", "the real geometry.buffer executor should complete the seeded-service job");
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException($"Timed out waiting for GPServer job '{jobId}' to succeed.");
    }

    private static async Task DeleteControlPlaneKeysAsync(string redisConnectionString)
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
        var database = multiplexer.GetDatabase();
        var endpoints = multiplexer.GetEndPoints();
        var server = multiplexer.GetServer(endpoints[0]);
        var keys = server.Keys(pattern: "controlplane:*").ToArray();
        if (keys.Length > 0)
        {
            await database.KeyDeleteAsync(keys);
        }
    }
}
