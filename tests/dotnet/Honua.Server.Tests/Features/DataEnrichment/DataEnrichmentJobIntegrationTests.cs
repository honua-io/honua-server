// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Geoprocessing;
using Honua.ControlPlane;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.DataEnrichment;

/// <summary>
/// End-to-end coverage for the async batch enrichment job (#2283): the
/// <c>enrichment.enrich</c> process is submitted through the canonical OGC API
/// Processes execution endpoint with the enrichment vocabulary (datasetId, method,
/// outputFields), executed by the durable job runtime against the real PostGIS
/// seed layers (the enrichment dataset points at seed join layer 1, the source at
/// seed layer 0 — the same wiring as the synchronous <c>POST /api/enrich</c>
/// tests), and its lifecycle is exercised through the EXISTING job endpoints:
/// submit → poll → fetch results → dismiss. Also covers the license/edition gate
/// on the job path and the large-input handoff: the sync endpoint's 413 names this
/// async process, and the same over-cap request succeeds as a job.
/// </summary>
[Collection("Redis")]
[Protocol(ProtocolNames.OgcApiProcesses)]
public sealed class DataEnrichmentJobIntegrationTests(RedisFixture redis)
{
    private const string DatasetKey = "test-boundaries";
    private const string ProcessId = "enrichment.enrich";
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task EnrichJob_SubmitPollFetchResults_ReturnsEnrichedFeatureCollectionWithProvenance()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture(HonuaEdition.Pro);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            var jobId = await SubmitJobAsync(client, ExecuteRequestBody());

            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be("successful");

            using var resultsDoc = await GetResultsAsync(client, jobId);
            var collection = DecodeOutputFeatureCollection(resultsDoc);

            // Catalog provenance travels with the artifact (#2283): dataset id and the
            // effective method are foreign members on the FeatureCollection envelope.
            collection.GetProperty("datasetId").GetString().Should().Be(DatasetKey);
            collection.GetProperty("method").GetString().Should().Be("intersects");

            var features = collection.GetProperty("features");
            features.GetArrayLength().Should().BeGreaterThan(0);
            foreach (var feature in features.EnumerateArray())
            {
                feature.GetProperty("properties").TryGetProperty("JOIN_COUNT", out var joinCount).Should().BeTrue();
                joinCount.GetInt64().Should().BeGreaterOrEqualTo(0);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("DELETE /ogc/processes/jobs/{jobId}")]
    public async Task EnrichJob_Dismiss_UsesExistingJobLifecycleEndpoints()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture(HonuaEdition.Pro);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            var jobId = await SubmitJobAsync(client, ExecuteRequestBody());

            // The worker may complete the small seed-layer job before the DELETE
            // lands, so accept both canonical outcomes of the shared lifecycle:
            // a dismissal (200 + dismissed) or the OGC terminal-state conflict (409).
            using var dismissResponse = await client.DeleteAsync($"/ogc/processes/jobs/{jobId}");
            dismissResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);
            using var dismissDoc = JsonDocument.Parse(await dismissResponse.Content.ReadAsStringAsync());
            if (dismissResponse.StatusCode == HttpStatusCode.OK)
            {
                dismissDoc.RootElement.GetProperty("status").GetString().Should().Be("dismissed");
            }
            else
            {
                dismissDoc.RootElement.GetProperty("detail").GetString().Should().Contain("terminal");
            }
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task EnrichJob_CommunityEdition_FailsWithEntitlementGate()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture(HonuaEdition.Community);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            var jobId = await SubmitJobAsync(client, ExecuteRequestBody());

            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be(
                "failed", "the Pro-tier enrichment entitlement is enforced on the job path");
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /api/enrich")]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task EnrichJob_LargeInput_SyncReturns413NamingAsyncPath_AsyncJobSucceeds()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        // Cap the synchronous path at 1 input feature so the seed layer trips the
        // 413 guard, then run the SAME enrichment as a job — the async path has no
        // synchronous input cap.
        var fixture = BuildFixture(HonuaEdition.Pro, extraConfig: new Dictionary<string, string?>
        {
            ["Limits:Analytics:MaxInputFeatures"] = "1",
        });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            var syncPayload = JsonSerializer.Serialize(new
            {
                datasetKey = DatasetKey,
                sourceLayerId = WebAppFixture.TestLayerId,
                method = "intersects",
            });
            using var syncContent = new StringContent(syncPayload, Encoding.UTF8, "application/json");
            using var syncResponse = await client.PostAsync("/api/enrich", syncContent);

            syncResponse.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
            var syncBody = await syncResponse.Content.ReadAsStringAsync();
            syncBody.Should().Contain(ProcessId, "the sync over-limit error must reference the async batch path");

            var jobId = await SubmitJobAsync(client, ExecuteRequestBody());

            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be(
                "successful", "inputs above the synchronous cap must succeed on the async job path");

            using var resultsDoc = await GetResultsAsync(client, jobId);
            var collection = DecodeOutputFeatureCollection(resultsDoc);
            collection.GetProperty("features").GetArrayLength().Should().BeGreaterThan(1);
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers (mirrors VectorProcessParityIntegrationTests' durable-runtime wiring)
    // -------------------------------------------------------------------------

    private WebAppFixture BuildFixture(HonuaEdition edition, Dictionary<string, string?>? extraConfig = null)
        => new WebAppFixture()
            .WithTestLicense(edition)
            .ConfigureWebHost(builder =>
            {
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    var config = new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:redis"] = redis.ConnectionString,
                        ["DataEnrichment:Datasets:0:Key"] = DatasetKey,
                        ["DataEnrichment:Datasets:0:DisplayName"] = "Test Boundaries",
                        ["DataEnrichment:Datasets:0:Category"] = "boundary",
                        ["DataEnrichment:Datasets:0:LayerId"] = "1",
                        ["DataEnrichment:Datasets:0:Predicate"] = "intersects",
                    };
                    if (extraConfig is not null)
                    {
                        foreach (var (key, value) in extraConfig)
                        {
                            config[key] = value;
                        }
                    }

                    configBuilder.AddInMemoryCollection(config);
                });
            })
            .ConfigureServices(WireDurableRuntime);

    private void WireDurableRuntime(IServiceCollection services)
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

        // Activate the worker host: the production dispatcher registered by
        // AddGeoprocessing routes enrichment.enrich to EnrichmentJobExecutor —
        // that executor is the system-under-test for this suite.
        services.AddJobWorker();
    }

    // OGC execution request carrying the enrichment vocabulary: dataset id, the
    // registered source layer, and the enrichment method.
    private static string ExecuteRequestBody() => JsonSerializer.Serialize(new
    {
        response = "document",
        inputs = new Dictionary<string, object>
        {
            ["datasetId"] = DatasetKey,
            ["layerId"] = WebAppFixture.TestLayerId,
            ["method"] = "intersects",
        },
    });

    private static async Task<string> SubmitJobAsync(HttpClient client, string body)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/ogc/processes/processes/{ProcessId}/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var submit = await client.SendAsync(request);
        submit.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
        var jobId = doc.RootElement.GetProperty("jobID").GetString();
        jobId.Should().NotBeNullOrWhiteSpace();
        return jobId!;
    }

    private static async Task<JsonDocument> PollUntilTerminalAsync(HttpClient client, string jobId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/ogc/processes/jobs/{jobId}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.GetProperty("status").GetString();
            if (status is "successful" or "failed" or "dismissed")
            {
                return JsonDocument.Parse(body);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException($"Timed out waiting for job '{jobId}' to reach a terminal status.");
    }

    private static async Task<JsonDocument> GetResultsAsync(HttpClient client, string jobId)
    {
        using var response = await client.GetAsync($"/ogc/processes/jobs/{jobId}/results");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement DecodeOutputFeatureCollection(JsonDocument resultsDoc)
    {
        var output = resultsDoc.RootElement.GetProperty("outputFeatureLayer");
        output.GetProperty("kind").GetString().Should().Be("FeatureLayer");
        output.GetProperty("type").GetString().Should().Be("application/geo+json");

        var href = output.GetProperty("href").GetString();
        href.Should().NotBeNull();
        href!.Should().StartWith(DataUriPrefix);

        var bytes = Convert.FromBase64String(href[DataUriPrefix.Length..]);
        using var doc = JsonDocument.Parse(bytes);
        var collection = doc.RootElement.Clone();
        collection.GetProperty("type").GetString().Should().Be("FeatureCollection");
        collection.GetProperty("processId").GetString().Should().Be(ProcessId);
        return collection;
    }

    private static async Task DeleteControlPlaneKeysAsync(string redisConnectionString)
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
        var database = multiplexer.GetDatabase();
        var endpoints = multiplexer.GetEndPoints();
        if (endpoints.Length == 0)
        {
            return;
        }

        var server = multiplexer.GetServer(endpoints[0]);
        var keys = server.Keys(pattern: "controlplane:*").ToArray();
        if (keys.Length > 0)
        {
            await database.KeyDeleteAsync(keys);
        }
    }
}
