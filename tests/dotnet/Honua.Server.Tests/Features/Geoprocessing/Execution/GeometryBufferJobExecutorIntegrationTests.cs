// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.ControlPlane;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// End-to-end coverage of the production <c>geometry.buffer</c> executor wired
/// into the durable runtime. Prior to this slice every submitted geoprocessing
/// job hit <c>NoExecutorForKind</c> and was abandoned with empty results; the
/// tests below pin the new behaviour so a regression cannot re-introduce that
/// failure mode.
/// </summary>
[Collection("Redis")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class GeometryBufferJobExecutorIntegrationTests(RedisFixture redis)
{
    // POINT(0 0) in little-endian WKB: 01 01000000 + 16 zero bytes.
    private const string PointWkbBase64 = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const double BufferDistance = 25.5;
    private const int Srid = 4326;

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task BufferProcess_CompletesAndReturnsBufferedGeometry()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture();
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            var jobId = await SubmitBufferJobAsync(client, BufferDistance);

            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be("successful");

            using var resultsResponse = await client.GetAsync($"/ogc/processes/jobs/{jobId}/results");
            resultsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var resultsDoc = JsonDocument.Parse(await resultsResponse.Content.ReadAsStringAsync());

            var output = resultsDoc.RootElement.GetProperty("outputFeatureLayer");
            output.GetProperty("id").GetString().Should().Be($"{jobId}:artifact:1");
            output.GetProperty("kind").GetString().Should().Be("FeatureLayer");
            output.GetProperty("type").GetString().Should().Be("application/geo+json");

            var href = output.GetProperty("href").GetString()!;
            href.Should().StartWith("data:application/geo+json;base64,");

            var feature = DecodeFeatureFromDataUri(href);
            feature.GetProperty("type").GetString().Should().Be("Feature");
            feature.GetProperty("properties").GetProperty("processId").GetString().Should().Be("geometry.buffer");
            feature.GetProperty("properties").GetProperty("inputSrid").GetInt32().Should().Be(Srid);
            feature.GetProperty("properties").GetProperty("bufferDistance").GetDouble()
                .Should().BeApproximately(BufferDistance, 1e-9);

            var geometry = ReadGeometry(feature.GetProperty("geometry"));
            geometry.IsEmpty.Should().BeFalse();
            geometry.Area.Should().BeGreaterThan(0d, "buffering a point must produce a positive-area polygon");

            // Buffering a point with NTS default quadrant-segment count yields an
            // area that approximates π r²; allow a tolerance because the polygon
            // discretizes the circle.
            var expectedArea = Math.PI * BufferDistance * BufferDistance;
            geometry.Area.Should().BeInRange(expectedArea * 0.9, expectedArea * 1.01);

            var resultStore = fixture.GetService<IGeoprocessingResultPackageStore>();
            var package = await resultStore.GetAsync(jobId);
            package.Should().NotBeNull();
            package!.Status.Should().Be(GeoprocessingWorkflowStatus.Completed);
            package.Artifacts.Should().ContainSingle();
            package.Artifacts[0].Uri.Should().StartWith("data:application/geo+json;base64,");
            package.Artifacts[0].Label.Should().Be("outputFeatureLayer");

            // Per-parameter GPServer-style retrieval (the OGC document mode does
            // not expose a per-parameter route, but the artifact must be the same
            // shape the GeoServices GPServer route surfaces.) The result document
            // routes back the same artifact id used by GPServer's results
            // dictionary, which exercises the protocol-neutral artifact projection.
            output.GetProperty("id").GetString().Should().Be(package.Artifacts[0].ArtifactId);
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }


    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private WebAppFixture BuildFixture()
        => new WebAppFixture()
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

        // Activate the worker host. The production GeometryBufferJobExecutor
        // registered by AddGeoprocessing is intentionally not replaced here —
        // it is the system-under-test.
        services.AddJobWorker();
    }

    private static async Task<string> SubmitBufferJobAsync(HttpClient client, double distance)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/ogc/processes/processes/geometry.buffer/execution");
        request.Headers.Add("Prefer", "respond-async");
        var body = string.Format(
            CultureInfo.InvariantCulture,
            "{{\"response\":\"document\",\"inputs\":{{\"wkb\":\"{0}\",\"srid\":{1},\"distance\":{2}}}}}",
            PointWkbBase64,
            Srid,
            distance.ToString("R", CultureInfo.InvariantCulture));
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

    private static JsonElement DecodeFeatureFromDataUri(string dataUri)
    {
        const string prefix = "data:application/geo+json;base64,";
        var base64 = dataUri[prefix.Length..];
        var bytes = Convert.FromBase64String(base64);
        var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }

    private static Geometry ReadGeometry(JsonElement geometryElement)
    {
        var reader = new GeoJsonReader();
        return reader.Read<Geometry>(geometryElement.GetRawText());
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
