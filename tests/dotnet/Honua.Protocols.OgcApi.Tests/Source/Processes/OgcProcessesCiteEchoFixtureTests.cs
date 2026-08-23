// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.ControlPlane;
using Honua.Protocols.Ogc.Api.Processes;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes;

/// <summary>
/// Exercises the suite-owned OGC API Processes echo fixture through the real durable
/// job submission, Redis store, worker, status, and result projection path.
/// </summary>
[Collection("Redis")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class OgcProcessesCiteEchoFixtureTests(RedisFixture redis)
{
    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Operation(Operations.ProcessExecution)]
    [Operation(Operations.JobStatus)]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /ogc/processes/processes/{processId}")]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task CiteProfile_EchoFixture_UsesCanonicalDurableRuntimeAndReturnsDeterministicValues()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = CreateFixture(redis.ConnectionString);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            using var descriptionResponse = await client.GetAsync(
                "/ogc/processes/processes/honua-cite-echo");
            descriptionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var description = JsonDocument.Parse(
                await descriptionResponse.Content.ReadAsStringAsync());
            var inputs = description.RootElement.GetProperty("inputs");
            inputs.GetProperty("literal").GetProperty("schema").GetProperty("type")
                .GetString().Should().Be("string");
            inputs.GetProperty("object").GetProperty("schema").GetProperty("properties")
                .TryGetProperty("value", out _).Should().BeTrue();
            var binaryAlternatives = inputs.GetProperty("binary").GetProperty("schema")
                .GetProperty("oneOf");
            binaryAlternatives.GetArrayLength().Should().Be(3);
            binaryAlternatives[0].GetProperty("format").GetString().Should().Be("byte");
            binaryAlternatives[1].GetProperty("required")[0].GetString().Should().Be("value");
            binaryAlternatives[2].GetProperty("required")[0].GetString().Should().Be("href");
            inputs.GetProperty("mixed").GetProperty("schema").GetProperty("oneOf")
                .GetArrayLength().Should().Be(2);
            inputs.GetProperty("array").GetProperty("schema").GetProperty("oneOf")[1]
                .GetProperty("items")
                .GetProperty("type").GetString().Should().Be("string");
            inputs.GetProperty("bbox").GetProperty("schema").GetProperty("properties")
                .TryGetProperty("bbox", out _).Should().BeTrue();
            inputs.GetProperty("pause").GetProperty("schema").GetProperty("type")
                .GetString().Should().Be("integer");
            inputs.GetProperty("pause").GetProperty("schema").GetProperty("minimum")
                .GetDouble().Should().Be(0);
            inputs.GetProperty("pause").GetProperty("schema").GetProperty("maximum")
                .GetDouble().Should().Be(OgcProcessesCiteEchoFixture.MaximumPauseSeconds);
            inputs.GetProperty("literal").GetProperty("minOccurs").GetInt32().Should().Be(1);
            inputs.GetProperty("object").GetProperty("minOccurs").GetInt32().Should().Be(0);
            description.RootElement.GetProperty("outputs").GetProperty("binary")
                .GetProperty("schema").GetProperty("oneOf").GetArrayLength().Should().Be(3);

            using var invalidBinary = await PostExecutionAsync(client, """
                {
                  "inputs": {
                    "literal": "teststring",
                    "binary": { "type": "wrong/type_not-a-tiff" }
                  },
                  "outputs": { "literal": { "transmissionMode": "value" } }
                }
                """);
            invalidBinary.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            using var invalidBinaryError = JsonDocument.Parse(
                await invalidBinary.Content.ReadAsStringAsync());
            invalidBinaryError.RootElement.GetProperty("detail").GetString()
                .Should().Contain("binary");

            using var invalidBinaryString = await PostExecutionAsync(client, """
                {
                  "inputs": {
                    "literal": "teststring",
                    "binary": "not-base64"
                  },
                  "outputs": { "binary": { "transmissionMode": "value" } }
                }
                """);
            invalidBinaryString.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            using var invalidBinaryStringError = JsonDocument.Parse(
                await invalidBinaryString.Content.ReadAsStringAsync());
            invalidBinaryStringError.RootElement.GetProperty("detail").GetString()
                .Should().Contain("base64");

            using var inlineBinarySubmit = await PostExecutionAsync(client, """
                {
                  "response": "document",
                  "inputs": {
                    "literal": "true",
                    "binary": "dGVzdA==",
                    "mixed": "null"
                  },
                  "outputs": {
                    "literal": { "transmissionMode": "value" },
                    "binary": { "transmissionMode": "value" },
                    "mixed": { "transmissionMode": "value" }
                  }
                }
                """);
            inlineBinarySubmit.StatusCode.Should().Be(HttpStatusCode.Created);
            var inlineBinaryJobId = await ReadJobIdAsync(inlineBinarySubmit);
            using var inlineBinaryTerminal = await PollUntilSucceededAsync(client, inlineBinaryJobId);
            inlineBinaryTerminal.RootElement.GetProperty("status").GetString()
                .Should().Be("successful");
            using var inlineBinaryResultsResponse = await client.GetAsync(
                $"/ogc/processes/jobs/{inlineBinaryJobId}/results");
            inlineBinaryResultsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var inlineBinaryResults = JsonDocument.Parse(
                await inlineBinaryResultsResponse.Content.ReadAsStringAsync());
            inlineBinaryResults.RootElement.GetProperty("literal").GetString()
                .Should().Be("true");
            inlineBinaryResults.RootElement.GetProperty("binary").GetString()
                .Should().Be("dGVzdA==");
            inlineBinaryResults.RootElement.GetProperty("mixed").GetString()
                .Should().Be("null");

            using var submit = await PostExecutionAsync(client, """
                {
                  "response": "document",
                  "inputs": {
                    "literal": "teststring",
                    "object": { "value": "teststring" },
                    "binary": {
                      "value": "dGVzdA==",
                      "format": { "mediaType": "image/tiff", "encoding": "base64" }
                    },
                    "mixed": { "value": "teststring" },
                    "array": ["test1", "test2", "test3"],
                    "bbox": {
                      "bbox": [51.9, 7.0, 52.0, 7.1],
                      "crs": "http://www.opengis.net/def/crs/OGC/1.3/CRS84"
                    }
                  },
                  "outputs": {
                    "literal": { "transmissionMode": "value" },
                    "object": { "transmissionMode": "value" },
                    "binary": { "transmissionMode": "value" },
                    "mixed": { "transmissionMode": "value" },
                    "array": { "transmissionMode": "value" },
                    "bbox": { "transmissionMode": "value" }
                  }
                }
                """);
            submit.StatusCode.Should().Be(HttpStatusCode.Created);
            var jobId = await ReadJobIdAsync(submit);

            using var terminal = await PollUntilSucceededAsync(client, jobId);
            terminal.RootElement.GetProperty("processID").GetString()
                .Should().Be("honua-cite-echo");

            using var resultResponse = await client.GetAsync(
                $"/ogc/processes/jobs/{jobId}/results");
            resultResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var results = JsonDocument.Parse(await resultResponse.Content.ReadAsStringAsync());
            results.RootElement.GetProperty("literal").GetString().Should().Be("teststring");
            results.RootElement.GetProperty("object").GetProperty("value").GetString()
                .Should().Be("teststring");
            results.RootElement.GetProperty("binary").GetProperty("format")
                .GetProperty("mediaType").GetString().Should().Be("image/tiff");
            results.RootElement.GetProperty("mixed").GetProperty("value").GetString()
                .Should().Be("teststring");
            results.RootElement.GetProperty("array").GetArrayLength().Should().Be(3);
            results.RootElement.GetProperty("bbox").GetProperty("bbox").GetArrayLength()
                .Should().Be(4);

            var jobStore = fixture.GetService<IExecutionJobStore>();
            var durableJob = await jobStore.GetAsync(jobId);
            durableJob.Should().NotBeNull();
            durableJob!.Status.Should().Be(ExecutionJobStatus.Succeeded);
            durableJob.Spec.Parameters.GetValueOrDefault("submittedVia")
                .Should().Be("OGC-API-Processes");
            durableJob.Spec.Parameters.GetValueOrDefault("protocolProcessId")
                .Should().Be("honua-cite-echo");
            durableJob.Spec.Parameters.GetValueOrDefault("process.output.0")
                .Should().Be("literal");

            using var pausedSubmit = await PostExecutionAsync(client, """
                {
                  "response": "document",
                  "inputs": { "literal": "teststring", "pause": 2 }
                }
                """);
            pausedSubmit.StatusCode.Should().Be(HttpStatusCode.Created);
            var pausedJobId = await ReadJobIdAsync(pausedSubmit);

            using var earlyResults = await client.GetAsync(
                $"/ogc/processes/jobs/{pausedJobId}/results");
            earlyResults.StatusCode.Should().Be(HttpStatusCode.NotFound);
            using var earlyError = JsonDocument.Parse(await earlyResults.Content.ReadAsStringAsync());
            earlyError.RootElement.GetProperty("type").GetString().Should().EndWith("result-not-ready");

            using var pausedTerminal = await PollUntilSucceededAsync(client, pausedJobId);
            pausedTerminal.RootElement.GetProperty("status").GetString().Should().Be("successful");
            using var pausedResultsResponse = await client.GetAsync(
                $"/ogc/processes/jobs/{pausedJobId}/results");
            pausedResultsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var pausedResults = JsonDocument.Parse(
                await pausedResultsResponse.Content.ReadAsStringAsync());
            pausedResults.RootElement.EnumerateObject().Select(property => property.Name)
                .Should().Equal("literal");
            pausedResults.RootElement.GetProperty("literal").GetString().Should().Be("teststring");
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    [Theory]
    [InlineData("{\"literal\":1}", "literal")]
    [InlineData("{\"literal\":\"ok\",\"object\":\"text\"}", "object")]
    [InlineData("{\"literal\":\"ok\",\"mixed\":42}", "mixed")]
    [InlineData("{\"literal\":\"ok\",\"array\":[\"ok\",1]}", "array")]
    [InlineData("{\"literal\":\"ok\",\"bbox\":{\"bbox\":[1,\"bad\"]}}", "bbox")]
    [InlineData("{\"literal\":\"ok\",\"pause\":11}", "pause")]
    [InlineData("{\"literal\":\"ok\",\"unknown\":true}", "unknown")]
    [InlineData("{}", "literal")]
    public void InputValidation_RejectsValuesOutsidePublishedSchemas(
        string json,
        string expectedInput)
    {
        using var document = JsonDocument.Parse(json);
        var inputs = document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());

        OgcProcessesCiteEchoFixture.TryValidateInputs(inputs, out var error)
            .Should().BeFalse();
        error.Should().Contain(expectedInput);
    }

    private static WebAppFixture CreateFixture(string redisConnectionString)
        => new WebAppFixture()
            .ConfigureServices(services =>
            {
                var certificationConfiguration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["HONUA_REGISTER_TEST_INFRASTRUCTURE"] = "true",
                        ["OgcProcesses:CertificationProfile"] = "ogcapi-processes10"
                    })
                    .Build();
                services.AddOgcProcesses(certificationConfiguration, "Test");

                services.RemoveAll<IConnectionMultiplexer>();
                services.AddSingleton<IConnectionMultiplexer>(
                    _ => ConnectionMultiplexer.Connect(redisConnectionString));

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
                services.AddSingleton<IQueueClaimReconciler>(
                    sp => sp.GetRequiredService<RedisJobQueue>());

                services.RemoveAll<IExecutionLogStore>();
                services.AddSingleton<IExecutionLogStore>(sp =>
                    new RedisExecutionLogStore(
                        sp.GetRequiredService<IConnectionMultiplexer>(),
                        sp.GetRequiredService<ILogger<RedisExecutionLogStore>>()));
                services.AddJobWorker();
            });

    private static async Task<HttpResponseMessage> PostExecutionAsync(HttpClient client, string json)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/ogc/processes/processes/honua-cite-echo/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static async Task<string> ReadJobIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("jobID").GetString()
            ?? throw new InvalidOperationException("Execution response did not contain a jobID.");
    }

    private static async Task<JsonDocument> PollUntilSucceededAsync(HttpClient client, string jobId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/ogc/processes/jobs/{jobId}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var status = document.RootElement.GetProperty("status").GetString();
            if (status == "successful")
            {
                return JsonDocument.Parse(body);
            }

            status.Should().NotBe("failed", "the certification fixture is deterministic");
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        throw new TimeoutException($"Timed out waiting for OGC process job '{jobId}'.");
    }

    private static async Task DeleteControlPlaneKeysAsync(string redisConnectionString)
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
        var database = multiplexer.GetDatabase();
        var endpoints = multiplexer.GetEndPoints();
        if (endpoints.Length == 0)
        {
            throw new InvalidOperationException("Redis connection string did not provide endpoints.");
        }

        var keys = multiplexer.GetServer(endpoints[0]).Keys(pattern: "controlplane:*").ToArray();
        if (keys.Length > 0)
        {
            await database.KeyDeleteAsync(keys);
        }
    }
}
