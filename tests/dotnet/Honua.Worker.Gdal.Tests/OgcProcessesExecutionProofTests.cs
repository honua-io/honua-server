// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Geoprocessing;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.Worker.Gdal.Execution;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using StackExchange.Redis;
using Xunit;
using Xunit.Sdk;
using TestProtocols = Honua.TestKit.Constants.ProtocolNames;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// The three execution cases relocated from OgcProcessesEndpointsTests for #4400.
/// Every case requires Redis, the production job loop and production executors.
/// RasterExecutionProof includes these public API proofs in the required PR Gate.
/// </summary>
[Trait("Category", "RasterExecutionProof")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class OgcProcessesExecutionProofTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task Execute_RasterSurfaceProcess_CompletesAndReturnsAnalyticalSlope()
    {
        var scratch = Path.Join(AppContext.BaseDirectory, "api-proof", Guid.NewGuid().ToString("N"));
        var runner = GdalProofRuntime.CreateRunner();
        await using var fixture = BuildFixture(runner, scratch);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            var source = await File.ReadAllBytesAsync(Path.Join(AppContext.BaseDirectory, "Fixtures", "SurfaceProof", "plane-hole.tif"));
            var body = "{\"inputs\":{\"source\":\"" + Convert.ToBase64String(source) + "\",\"units\":\"degrees\",\"zFactor\":2}}";
            using var results = await SubmitAndGetResults(client, "surface.slope", body);
            var output = results.RootElement.GetProperty("outputRaster");
            output.GetProperty("mediaType").GetString().Should().StartWith("image/tiff");
            output.GetProperty("encoding").GetString().Should().Be("base64");
            output.TryGetProperty("href", out _).Should().BeFalse();
            var bytes = Convert.FromBase64String(output.GetProperty("value").GetString()!);

            Directory.CreateDirectory(scratch);
            await File.WriteAllBytesAsync(Path.Join(scratch, "slope.tif"), bytes);
            File.Copy(Path.Join(AppContext.BaseDirectory, "Fixtures", "RasterProof", "decode.py"), Path.Join(scratch, "decode.py"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var decoded = await runner.RunAsync("python3", ["decode.py", "slope.tif"], scratch, timeout.Token);
            decoded.ExitCode.Should().Be(0, decoded.StandardError);
            using var raster = JsonDocument.Parse(decoded.StandardOutput);
            AssertSlope(raster.RootElement);

            // Challenge the same oracle with a plausible passthrough defect:
            // the input is a valid georeferenced GeoTIFF, but it is not slope.
            await File.WriteAllBytesAsync(Path.Join(scratch, "slope.tif"), source);
            var passthrough = await runner.RunAsync("python3", ["decode.py", "slope.tif"], scratch, timeout.Token);
            passthrough.ExitCode.Should().Be(0, passthrough.StandardError);
            using var wrong = JsonDocument.Parse(passthrough.StandardOutput);
            Action rejectPassthrough = () => AssertSlope(wrong.RootElement);
            rejectPassthrough.Should().Throw<XunitException>();
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public Task Execute_FirstSliceVectorProcess_CompletesAndReturnsBufferGeometry()
        => ProveBuffer(selectValue: false);

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public Task Execute_FirstSliceProcessWithValueOutputSelection_CompletesAndReturnsInlineBuffer()
        => ProveBuffer(selectValue: true);

    private async Task ProveBuffer(bool selectValue)
    {
        await using var fixture = BuildFixture();
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        // A non-origin center detects dropped or swapped coordinates. The planar
        // buffer radius is 25.5 input-CRS units; the oracle uses circle geometry,
        // never the production Buffer method or a captured output snapshot.
        var point = new Point(100, 200) { SRID = 3857 };
        var wkb = Convert.ToBase64String(new WKBWriter().Write(point));
        var selection = selectValue ? """, "outputs":{"outputFeatureLayer":{"transmissionMode":"value"}}""" : "";
        var body = "{\"inputs\":{\"wkb\":\"" + wkb + "\",\"srid\":3857,\"distance\":25.5}" + selection + "}";
        using var results = await SubmitAndGetResults(client, "geometry.buffer", body);
        var output = results.RootElement.GetProperty("outputFeatureLayer");
        output.TryGetProperty("href", out _).Should().BeFalse();
        var feature = output.GetProperty("value");
        feature.GetProperty("type").GetString().Should().Be("Feature");
        var properties = feature.GetProperty("properties");
        properties.GetProperty("processId").GetString().Should().Be("geometry.buffer");
        properties.GetProperty("inputSrid").GetInt32().Should().Be(3857);
        properties.GetProperty("bufferDistance").GetDouble().Should().Be(25.5);
        var geometry = new GeoJsonReader().Read<Geometry>(feature.GetProperty("geometry").GetRawText());
        geometry.Should().BeOfType<Polygon>();
        geometry.IsValid.Should().BeTrue();
        geometry.IsEmpty.Should().BeFalse();
        var polygon = (Polygon)geometry;
        polygon.NumInteriorRings.Should().Be(0);
        polygon.Centroid.X.Should().BeApproximately(100, 1e-8);
        polygon.Centroid.Y.Should().BeApproximately(200, 1e-8);
        polygon.EnvelopeInternal.MinX.Should().BeApproximately(74.5, 1e-8);
        polygon.EnvelopeInternal.MaxX.Should().BeApproximately(125.5, 1e-8);
        polygon.EnvelopeInternal.MinY.Should().BeApproximately(174.5, 1e-8);
        polygon.EnvelopeInternal.MaxY.Should().BeApproximately(225.5, 1e-8);
        // A polygonal approximation lies within 1% of the analytical disk area.
        polygon.Area.Should().BeInRange(0.99 * Math.PI * 25.5 * 25.5, Math.PI * 25.5 * 25.5);
        foreach (var coordinate in polygon.ExteriorRing.Coordinates)
        {
            var radius = Math.Sqrt(Math.Pow(coordinate.X - 100, 2) + Math.Pow(coordinate.Y - 200, 2));
            radius.Should().BeApproximately(25.5, 1e-8);
        }
    }

    private static void AssertSlope(JsonElement raster)
    {
        raster.GetProperty("driver").GetString().Should().Be("GTiff");
        raster.GetProperty("width").GetInt32().Should().Be(5);
        raster.GetProperty("height").GetInt32().Should().Be(5);
        raster.GetProperty("srid").GetInt32().Should().Be(3857);
        raster.GetProperty("transform").EnumerateArray().Select(v => v.GetDouble())
            .Should().Equal(1000, 2, 0, 2000, 0, -2);
        raster.GetProperty("bands").GetArrayLength().Should().Be(1);
        var band = raster.GetProperty("bands")[0];
        band.GetProperty("type").GetString().Should().Be("Float32");
        band.GetProperty("nodata").GetDouble().Should().Be(-9999);
        var values = band.GetProperty("values").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var mask = band.GetProperty("mask").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        values.Should().HaveCount(25);
        mask.Should().HaveCount(25);
        // z rises 2m east and 3m south per 2m pixel; zFactor is the
        // catalog's vertical/horizontal unit ratio. The (0,0) hole also
        // invalidates the (1,1) neighborhood; the outer edge is nodata.
        var expectedSlope = Math.Atan(Math.Sqrt(1 + 1.5 * 1.5) / 2) * 180 / Math.PI;
        for (var row = 0; row < 5; row++)
        {
            for (var col = 0; col < 5; col++)
            {
                var missing = row == 0 || row == 4 || col == 0 || col == 4 || (row == 1 && col == 1);
                values[row * 5 + col].Should().BeApproximately(missing ? -9999 : expectedSlope, 1e-5);
                mask[row * 5 + col].Should().Be(missing ? 0 : 255);
            }
        }
    }

    private WebAppFixture BuildFixture(IGdalCommandRunner? runner = null, string? scratch = null)
        => new WebAppFixture()
            .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:redis"] = redis.ConnectionString })))
            .ConfigureServices(services =>
            {
                services.RemoveAll<IConnectionMultiplexer>();
                services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis.ConnectionString));
                services.RemoveAll<IExecutionJobStore>();
                services.AddSingleton<IExecutionJobStore>(sp => new RedisExecutionJobStore(
                    sp.GetRequiredService<IConnectionMultiplexer>(), sp.GetRequiredService<ILogger<RedisExecutionJobStore>>()));
                services.RemoveAll<IGeoprocessingResultPackageStore>();
                services.AddSingleton<IGeoprocessingResultPackageStore>(sp => new RedisGeoprocessingResultPackageStore(
                    sp.GetRequiredService<IConnectionMultiplexer>(), sp.GetRequiredService<IOptionsMonitor<GeoprocessingExecutorOptions>>(),
                    sp.GetRequiredService<ILogger<RedisGeoprocessingResultPackageStore>>()));
                services.RemoveAll<RedisJobQueue>();
                services.RemoveAll<IJobQueue>();
                services.RemoveAll<IQueueClaimReconciler>();
                services.AddSingleton<RedisJobQueue>(sp => new RedisJobQueue(
                    sp.GetRequiredService<IConnectionMultiplexer>(), sp.GetRequiredService<IExecutionJobStore>(),
                    sp.GetRequiredService<ILogger<RedisJobQueue>>()));
                services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<RedisJobQueue>());
                services.AddSingleton<IQueueClaimReconciler>(sp => sp.GetRequiredService<RedisJobQueue>());
                services.RemoveAll<IExecutionLogStore>();
                services.AddSingleton<IExecutionLogStore>(sp => new RedisExecutionLogStore(
                    sp.GetRequiredService<IConnectionMultiplexer>(), sp.GetRequiredService<ILogger<RedisExecutionLogStore>>()));
                if (runner is not null)
                {
                    // Compose the real native dispatcher and executor in the test host.
                    // Only the CLI transport crosses Docker; no output is substituted.
                    services.RemoveAll<IJobExecutor>();
                    services.RemoveAll<IProcessExecutor>();
                    services.AddGdalProcessExecutors(new ConfigurationBuilder().AddInMemoryCollection(
                        new Dictionary<string, string?> { ["GdalWorker:ScratchRoot"] = scratch }).Build());
                    services.RemoveAll<IGdalCommandRunner>();
                    services.AddSingleton(runner);
                    services.AddSingleton<IJobExecutor, GdalDispatchJobExecutor>();
                }
                // The test host can register Redis after the serving composition's
                // Redis gate. Ensure the production queue drainer and terminal
                // callback are present, using the same services as AddJobWorker.
                services.TryAddSingleton<ExecutionJobCancellationTokens>();
                services.TryAddSingleton<IJobCancellationNotifier>(sp => sp.GetRequiredService<ExecutionJobCancellationTokens>());
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobTerminalCallback, GeoprocessingJobTerminalCallback>());
                services.AddHostedService<JobExecutionService>();
            });

    private static async Task<JsonDocument> SubmitAndGetResults(HttpClient client, string processId, string body)
    {
        client.Timeout = TimeSpan.FromSeconds(90);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/ogc/processes/processes/{processId}/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var submitted = await client.SendAsync(request);
        var submission = await submitted.Content.ReadAsStringAsync();
        submitted.StatusCode.Should().Be(HttpStatusCode.Created, submission);
        submitted.Headers.Location.Should().NotBeNull();
        using var document = JsonDocument.Parse(submission);
        var jobId = document.RootElement.GetProperty("jobID").GetString();
        jobId.Should().NotBeNullOrWhiteSpace();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var status = await client.GetAsync($"/ogc/processes/jobs/{jobId}");
            status.StatusCode.Should().Be(HttpStatusCode.OK);
            var statusBody = await status.Content.ReadAsStringAsync();
            using var terminal = JsonDocument.Parse(statusBody);
            terminal.RootElement.GetProperty("jobID").GetString().Should().Be(jobId);
            terminal.RootElement.GetProperty("processID").GetString().Should().Be(processId);
            var state = terminal.RootElement.GetProperty("status").GetString();
            if (state is "successful" or "failed" or "dismissed")
            {
                state.Should().Be("successful", statusBody);
                using var response = await client.GetAsync($"/ogc/processes/jobs/{jobId}/results");
                var resultBody = await response.Content.ReadAsStringAsync();
                response.StatusCode.Should().Be(HttpStatusCode.OK, resultBody);
                return JsonDocument.Parse(resultBody);
            }
            state.Should().BeOneOf("accepted", "running");
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
        throw new TimeoutException($"Job {jobId} did not reach a terminal state within 90 seconds.");
    }
}
