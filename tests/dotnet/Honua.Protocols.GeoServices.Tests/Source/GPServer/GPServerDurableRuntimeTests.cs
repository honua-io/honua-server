// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.ControlPlane;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>
/// Verifies the GPServer adapter against the real durable execution substrate.
/// </summary>
[Collection("Redis")]
[Protocol(TestProtocols.GPServer)]
public sealed class GPServerDurableRuntimeTests(RedisFixture redis)
{
    private const string PointWkbBase64 = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string ServiceId = WebAppFixture.TestServiceId;

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/results/{paramName}")]
    public async Task SubmitJob_WithRedisBackedRuntime_CompletesAndReturnsDurableResult()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = CreateDurableFixture(productionExecutor: false);

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

            submit.StatusCode.Should().Be(HttpStatusCode.OK);
            using var submitDoc = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
            var jobId = submitDoc.RootElement.GetProperty("jobId").GetString();
            jobId.Should().NotBeNullOrWhiteSpace();
            submitDoc.RootElement.GetProperty("jobStatus").GetString().Should().Be("esriJobSubmitted");

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
            resultRoot.GetProperty("value").GetProperty("url").GetString().Should().Be("https://example.test/durable-gp-output.geojson");

            var jobStore = fixture.GetService<IExecutionJobStore>();
            var durableJob = await jobStore.GetAsync(jobId!);
            durableJob.Should().NotBeNull();
            durableJob!.Status.Should().Be(ExecutionJobStatus.Succeeded);
            durableJob.Spec.Parameters.Should().Contain(new KeyValuePair<string, string>("submittedVia", "GPServer"));
            durableJob.Spec.Parameters.Should().Contain(new KeyValuePair<string, string>("gpserver.serviceId", ServiceId));
            durableJob.Spec.Parameters.Should().Contain(new KeyValuePair<string, string>("gpserver.taskName", "geometry.buffer"));
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

            status.Should().NotBe("esriJobFailed", "the configured durable runtime should complete the bounded test job");
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException($"Timed out waiting for GPServer job '{jobId}' to succeed.");
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
            await context.PublishArtifactAsync("https://example.test/durable-gp-output.geojson", cancellationToken);
            return JobExecutionResult.Succeeded();
        }
    }
    [IntegrationTheory]
    [InlineData("Execute")]
    [InlineData("SubmitJob")]
    [Operation(Operations.Create)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task SoapArea_WithProductionExecutor_ReturnsIndependentRectangleAreaAndMetadata(string operation)
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        var fixture = CreateDurableFixture(productionExecutor: true);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(45);
            // Independent OGC WKB encoding of a literal 3 by 4 rectangle.
            // The expected area is width * height, never copied from NTS output.
            using var bytes = new MemoryStream();
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write((byte)1);
                writer.Write(3u);
                writer.Write(1u);
                writer.Write(5u);
                foreach (var (x, y) in new[] { (0d, 0d), (3d, 0d), (3d, 4d), (0d, 4d), (0d, 0d) })
                {
                    writer.Write(x);
                    writer.Write(y);
                }
            }
            var arguments = $"""
                <ToolName>Honua_67656F6D657472792E61726561</ToolName><Values xsi:type="tns:GPValues">
                <GPValue xsi:type="tns:GPString"><Value>{Convert.ToBase64String(bytes.ToArray())}</Value></GPValue>
                <GPValue xsi:type="tns:GPLong"><Value>3857</Value></GPValue></Values>
                """;
            var result = await SendSoapAsync(client, operation, arguments);
            if (operation == "SubmitJob")
            {
                var jobId = result.Value;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true)
                {
                    var status = await SendSoapAsync(client, "GetJobStatus", $"<JobID>{jobId}</JobID>");
                    if (status.Value == "esriJobSucceeded")
                    {
                        break;
                    }
                    status.Value.Should().NotBeOneOf("esriJobFailed", "esriJobCancelled");
                    await Task.Delay(100, timeout.Token);
                }
                result = await SendSoapAsync(client, "GetJobResult", $"<JobID>{jobId}</JobID><ParameterNames><String>outputScalar</String></ParameterNames>");
            }
            var scalar = result.Element("Values")!.Elements("GPValue").Should().ContainSingle().Subject;
            scalar.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))!.Value.Should().Be("tns:GPString");
            var dataUri = scalar.Element("Value")!.Value;
            const string prefix = "data:application/json;base64,";
            dataUri.Should().StartWith(prefix);
            using var decoded = JsonDocument.Parse(Convert.FromBase64String(dataUri[prefix.Length..]));
            var measure = decoded.RootElement;
            measure.GetProperty("value").GetDouble().Should().Be(3 * 4);
            measure.GetProperty("type").GetString().Should().Be("MeasureResult");
            measure.GetProperty("processId").GetString().Should().Be("geometry.area");
            measure.GetProperty("measure").GetString().Should().Be("area");
            measure.GetProperty("unit").GetString().Should().Be("input-crs-units-squared");
            measure.GetProperty("inputSrid").GetInt32().Should().Be(3857);
            measure.GetProperty("inputGeometryType").GetString().Should().Be("Polygon");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<XElement> SendSoapAsync(HttpClient client, string operation, string arguments)
    {
        var xml = $"""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/" xmlns:tns="http://www.esri.com/schemas/ArcGIS/10.8" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
            <soap:Body><tns:{operation}>{arguments}</tns:{operation}></soap:Body></soap:Envelope>
            """;
        using var content = new StringContent(xml, Encoding.UTF8, "text/xml");
        using var response = await client.PostAsync($"/services/{ServiceId}/GPServer", content);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return XDocument.Parse(body).Descendants("Result").Single();
    }

    private WebAppFixture CreateDurableFixture(bool productionExecutor)
    {
        return new WebAppFixture()
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

                // Replace the production geometry.buffer executor registered by
                // AddGeoprocessing with a deterministic fixture so this test
                // exercises the GPServer protocol projection independently of
                // the buffer implementation.
                if (!productionExecutor)
                {
                    services.RemoveAll<IJobExecutor>();
                    services.AddSingleton<IJobExecutor, SuccessfulGpServerJobExecutor>();
                }
                services.AddJobWorker();
            });
    }

}
