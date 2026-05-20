// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Infrastructure.ControlPlane;
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
/// End-to-end parity coverage for the slice-2 vector executors. Each test
/// submits a process through the OGC API Processes execute endpoint, polls
/// the durable runtime until the job reaches a terminal status, retrieves
/// the result document, and compares the published GeoJSON Feature against a
/// golden expectation (geometry type, feature count, area / coordinate
/// position). Mirrors the slice-1 buffer integration test shape so the new
/// processes pin the same evidence contract.
/// </summary>
[Collection("Redis")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class VectorProcessParityIntegrationTests(RedisFixture redis)
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task ClipProcess_CompletesAndReturnsClippedSquare()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture();
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            var target = BuildBoxPolygon(0, 0, 10, 10);
            var clipEnvelope = BuildBoxPolygon(5, 5, 15, 15);
            var body = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"response\":\"document\",\"inputs\":{{\"targetWkb\":\"{0}\",\"clipEnvelopeWkb\":\"{1}\",\"srid\":4326}}}}",
                WkbBase64(target),
                WkbBase64(clipEnvelope));

            var jobId = await SubmitJobAsync(client, "geometry.clip", body);

            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be("successful");

            using var resultsDoc = await GetResultsAsync(client, jobId);
            var feature = DecodeOutputFeature(resultsDoc, "geometry.clip");

            feature.GetProperty("properties").GetProperty("inputSrid").GetInt32().Should().Be(4326);

            var geometry = ReadGeometry(feature.GetProperty("geometry"));
            geometry.Area.Should().BeApproximately(25.0, 1e-6,
                "clip of (0..10) by (5..15) envelope yields a 5x5 square");

            var resultStore = fixture.GetService<IGeoprocessingResultPackageStore>();
            var package = await resultStore.GetAsync(jobId);
            package.Should().NotBeNull();
            package!.Status.Should().Be(GeoprocessingWorkflowStatus.Completed);
            package.Artifacts.Should().ContainSingle();
            package.Artifacts[0].Uri.Should().StartWith(DataUriPrefix);
            package.Artifacts[0].Label.Should().Be("outputFeatureLayer");
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
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task IntersectProcess_CompletesAndReturnsOverlapSquare()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture();
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            var target = BuildBoxPolygon(0, 0, 10, 10);
            var intersector = BuildBoxPolygon(5, 5, 15, 15);
            var body = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"response\":\"document\",\"inputs\":{{\"targetWkb\":\"{0}\",\"intersectorWkb\":\"{1}\",\"srid\":4326}}}}",
                WkbBase64(target),
                WkbBase64(intersector));

            var jobId = await SubmitJobAsync(client, "geometry.intersect", body);

            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be("successful");

            using var resultsDoc = await GetResultsAsync(client, jobId);
            var feature = DecodeOutputFeature(resultsDoc, "geometry.intersect");

            var geometry = ReadGeometry(feature.GetProperty("geometry"));
            geometry.Area.Should().BeApproximately(25.0, 1e-6,
                "overlap of two 10x10 boxes offset by (5,5) is a 5x5 square");
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
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task ProjectProcess_IdentityTransform_RoundTripsCoordinates()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture();
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            // POINT(1 2) in WGS 84 — identity projection back to WGS 84.
            var point = NetTopologySuite.NtsGeometryServices.Instance
                .CreateGeometryFactory(srid: 4326)
                .CreatePoint(new Coordinate(1.0, 2.0));

            var body = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"response\":\"document\",\"inputs\":{{\"wkb\":\"{0}\",\"fromSrid\":4326,\"toSrid\":4326}}}}",
                WkbBase64(point));

            var jobId = await SubmitJobAsync(client, "geometry.project", body);

            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be("successful");

            using var resultsDoc = await GetResultsAsync(client, jobId);
            var feature = DecodeOutputFeature(resultsDoc, "geometry.project");

            feature.GetProperty("properties").GetProperty("fromSrid").GetInt32().Should().Be(4326);
            feature.GetProperty("properties").GetProperty("toSrid").GetInt32().Should().Be(4326);

            var geometry = ReadGeometry(feature.GetProperty("geometry"));
            ((Point)geometry).X.Should().BeApproximately(1.0, 1e-9);
            ((Point)geometry).Y.Should().BeApproximately(2.0, 1e-9);
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

        // Activate the worker host. The production dispatcher registered by
        // AddGeoprocessing routes the geometry.clip / geometry.intersect /
        // geometry.project ids to their per-process executors — that's the
        // system-under-test for this parity suite.
        services.AddJobWorker();
    }

    private static Polygon BuildBoxPolygon(double minX, double minY, double maxX, double maxY)
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        return factory.CreatePolygon(new[]
        {
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY),
        });
    }

    private static string WkbBase64(Geometry geometry)
        => Convert.ToBase64String(new WKBWriter().Write(geometry));

    private static async Task<string> SubmitJobAsync(HttpClient client, string processId, string body)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/ogc/processes/processes/{processId}/execution");
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

    private static JsonElement DecodeOutputFeature(JsonDocument resultsDoc, string expectedProcessId)
    {
        var output = resultsDoc.RootElement.GetProperty("outputFeatureLayer");
        output.GetProperty("kind").GetString().Should().Be("FeatureLayer");
        output.GetProperty("type").GetString().Should().Be("application/geo+json");

        var href = output.GetProperty("href").GetString();
        href.Should().NotBeNull();
        href!.Should().StartWith(DataUriPrefix);

        var base64 = href[DataUriPrefix.Length..];
        var bytes = Convert.FromBase64String(base64);
        var doc = JsonDocument.Parse(bytes);
        var feature = doc.RootElement.Clone();
        feature.GetProperty("type").GetString().Should().Be("Feature");
        feature.GetProperty("properties").GetProperty("processId").GetString()
            .Should().Be(expectedProcessId);
        return feature;
    }

    private static Geometry ReadGeometry(JsonElement geometryElement)
        => new GeoJsonReader().Read<Geometry>(geometryElement.GetRawText());

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
