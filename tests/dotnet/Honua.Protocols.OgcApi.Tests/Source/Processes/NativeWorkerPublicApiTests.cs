// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

extern alias NativeWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.TestKit.Attributes;
using NativeWorker::Honua.Worker.Gdal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes;

/// <summary>Public HTTP execution over the durable runtime and production native executor (#4401).</summary>
[Protocol(TestProtocols.OgcApiProcesses)]
[Collection(WebAppFixtureJobSubstrateExtensions.JobSubstrateCollection)]
public sealed class NativeWorkerPublicApiTests(ITestOutputHelper output)
{
    private const string DockerHostAlias = "host.docker.internal";

    [RequiredEnvironmentFact("HONUA_WORKER_IMAGE")]
    [Trait("Category", "NativePublicApi")]
    [Trait("Tier", "Integration")]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task VectorConvert_SubmitPollDecode_PreservesPropertiesAndThreeDimensionalCoordinates()
    {
        var configuration = new ConfigurationBuilder().Build();
        // The assembly alias makes both native families accessible to this web test project.
        // Execution below goes through the image's entrypoint and native dispatcher, not these leaves.
        var workerServices = new ServiceCollection();
        workerServices.AddLogging();
        workerServices.AddGdalProcessExecutors(configuration);
        await using var registration = workerServices.BuildServiceProvider();
        registration.GetServices<IProcessExecutor>().Should().Contain(e => e.ProcessIds.Contains("gdal.ogr2ogr"));
        registration.GetServices<IProcessExecutor>().Should().Contain(e => e.ProcessIds.Contains("pcloud.translate"));

        var image = Environment.GetEnvironmentVariable("HONUA_WORKER_IMAGE")!;
        var fixture = NoRedisJobHost.Create();
        await NoRedisJobHost.StartAsync(fixture);
        await using var nativeWorker = new ContainerBuilder()
            .WithImage(image)
            .WithExtraHost(DockerHostAlias, "host-gateway")
            .WithEnvironment(
                "ConnectionStrings__DefaultConnection",
                ConnectionStringReachableFromContainer(fixture.Postgres.ConnectionString))
            .Build();
        try
        {
            await nativeWorker.StartAsync();
            output.WriteLine($"Production worker image: {image}");

            using var client = fixture.CreateAdminClient();
            // Independent CSV input and explicitly specified GeoJSON oracle: a passthrough,
            // dropped Z, swapped axes, missing row, or changed attribute cannot satisfy this.
            const string csv = "WKT,name,value\n\"POINT Z (12.25 -4.5 7.75)\",alpha,17\n\"POINT Z (-23.5 8.25 -2.5)\",beta,29\n";
            var source = Convert.ToBase64String(Encoding.UTF8.GetBytes(csv));
            var body = $$$"""{"response":"document","inputs":{"source":"{{{source}}}","sourceFormat":"CSV","targetFormat":"GeoJSON"}}""";
            using var request = new HttpRequestMessage(HttpMethod.Post, "/ogc/processes/processes/gdal.ogr2ogr/execution");
            request.Headers.Add("Prefer", "respond-async");
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var submit = await client.SendAsync(request);
            var submitBody = await submit.Content.ReadAsStringAsync();
            submit.StatusCode.Should().Be(HttpStatusCode.Created, submitBody);
            using var submitted = JsonDocument.Parse(submitBody);
            var jobId = submitted.RootElement.GetProperty("jobID").GetString()!;
            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be("successful", terminal.RootElement.GetRawText());
            using var response = await client.GetAsync($"/ogc/processes/jobs/{jobId}/results");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var results = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var processOutput = results.RootElement.EnumerateObject().Should().ContainSingle().Which.Value;
            processOutput.GetProperty("mediaType").GetString().Should().Be("application/geo+json");
            var collection = processOutput.GetProperty("value");
            collection.GetProperty("type").GetString().Should().Be("FeatureCollection");
            var features = collection.GetProperty("features").EnumerateArray().ToArray();
            features.Should().HaveCount(2);
            AssertPoint(features, "alpha", "17", [12.25, -4.5, 7.75]);
            AssertPoint(features, "beta", "29", [-23.5, 8.25, -2.5]);
        }
        finally
        {
            try
            {
                var logs = await nativeWorker.GetLogsAsync();
                output.WriteLine(logs.Stdout);
                output.WriteLine(logs.Stderr);
            }
            finally
            {
                await fixture.DisposeAsync();
            }
        }
    }

    private static void AssertPoint(JsonElement[] features, string name, string value, double[] coordinates)
    {
        var feature = features.Single(f => f.GetProperty("properties").GetProperty("name").GetString() == name);
        feature.GetProperty("properties").GetProperty("value").GetString().Should().Be(value);
        var geometry = feature.GetProperty("geometry");
        geometry.GetProperty("type").GetString().Should().Be("Point");
        geometry.GetProperty("coordinates").EnumerateArray().Select(c => c.GetDouble()).Should().Equal(coordinates);
    }

    // The test database is published on the Docker host, which a container reaches through the host gateway.
    private static string ConnectionStringReachableFromContainer(string hostConnectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(hostConnectionString);
        if (builder.Host is "localhost" or "127.0.0.1" or "::1")
        {
            builder.Host = DockerHostAlias;
        }

        return builder.ConnectionString;
    }

    private static async Task<JsonDocument> PollUntilTerminalAsync(HttpClient client, string jobId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        string? lastResponse = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/ogc/processes/jobs/{jobId}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();
            lastResponse = body;
            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.GetProperty("status").GetString();
            if (status is "successful" or "failed" or "dismissed")
            {
                return JsonDocument.Parse(body);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException($"Timed out waiting for job '{jobId}' to reach a terminal status. Last response: {lastResponse}");
    }

}
