// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Honua.Protocols.Ogc.Classic.Wps20;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

[Collection("Unit")]
[Trait("Tier", "Fast")]
public sealed class ServingObservabilityContractTests
{
    [Theory]
    [InlineData("/rest/services/demo/FeatureServer/0/query", 400, 200, true)]
    [InlineData("/rest/services/demo/MapServer/query", 500, 200, true)]
    [InlineData("/ogc/features/collections/demo/items", 400, 400, false)]
    public async Task ErrorEnvelope_RecordsTheExecutedTransportStatus(string path, int errorCode, int transportStatus, bool inBand)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("example.test");
        await using var body = new MemoryStream();
        context.Response.Body = body;
        using var metrics = new Metrics();

        await StandardErrorResponseFormatter.WriteErrorAsync(context,
            new StandardErrorResponse(errorCode, "Test error", "Synthetic invalid input"));

        Assert.Equal(transportStatus, context.Response.StatusCode);
        body.Position = 0;
        using var json = await JsonDocument.ParseAsync(body);
        Assert.Equal(errorCode, inBand
            ? json.RootElement.GetProperty("error").GetProperty("code").GetInt32()
            : json.RootElement.GetProperty("status").GetInt32());
        var error = Assert.Single(metrics.Samples.Where(sample => sample.Name == "honua_request_error_total"));
        Assert.Equal(1, error.Value);
        Assert.Equal(errorCode, error.Tags["error_code"]);
        Assert.Equal(inBand, error.Tags["in_band"]);
    }

    [Theory]
    [InlineData("GET", "GetCapabilities", 200, "wps.getcapabilities")]
    [InlineData("POST", "GetCapabilities", 200, "wps.getcapabilities")]
    [InlineData("GET", "DescribeProcess", 404, "wps.describeprocess")]
    [InlineData("POST", "DescribeProcess", 404, "wps.describeprocess")]
    public async Task WpsEndpoint_RecordsServingLatencyAndRequestDenominator(string method, string operation, int statusCode, string telemetryOperation)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.WebHost.UseTestServer();
        var catalog = Substitute.For<IProcessCatalog>();
        catalog.ListProcesses().Returns(Array.Empty<ProcessDefinition>());
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton(Substitute.For<IGeoprocessingJobService>());
        builder.Services.AddSingleton<Wps20ConformanceEcho>();
        builder.Services.AddOptions<Wps20Options>();
        await using var app = builder.Build();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.MapWps20Endpoint();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var metrics = new Metrics();
        var before = HonuaTelemetry.GetServingLatencySnapshot().Protocols
            .SingleOrDefault(protocol => protocol.Protocol == "WPS-2.0.2")?.RequestCount ?? 0;
        using var request = new HttpRequestMessage(new HttpMethod(method),
            $"/wps?service=WPS&version=2.0.0&request={operation}&identifier=missing");
        if (method == "POST")
        {
            // Omit KVP operation on POST: telemetry must retain the parsed XML operation.
            request.RequestUri = new Uri("/wps", UriKind.Relative);
            var identifier = operation == "DescribeProcess" ? "<ows:Identifier>missing</ows:Identifier>" : string.Empty;
            request.Content = new StringContent(
                $"<wps:{operation} xmlns:wps=\"http://www.opengis.net/wps/2.0\" xmlns:ows=\"http://www.opengis.net/ows/2.0\" service=\"WPS\" version=\"2.0.0\">{identifier}</wps:{operation}>",
                Encoding.UTF8, "application/xml");
        }

        using var response = await client.SendAsync(request);
        var xml = await response.Content.ReadAsStringAsync();
        await app.StopAsync();

        Assert.Equal(statusCode, (int)response.StatusCode);
        Assert.Contains(statusCode == 200 ? "Capabilities" : "NoSuchProcess", xml, StringComparison.Ordinal);
        var serving = Assert.Single(metrics.Samples.Where(sample => sample.Name == "honua_serving_request_duration_ms"));
        Assert.True(serving.Value >= 0);
        Assert.Equal("WPS-2.0.2", serving.Tags[HonuaTelemetry.Tags.Protocol]);
        Assert.Equal(telemetryOperation, serving.Tags[HonuaTelemetry.Tags.Operation]);
        Assert.Equal(statusCode == 200 ? "2xx" : "4xx", serving.Tags["status_class"]);
        var after = Assert.Single(HonuaTelemetry.GetServingLatencySnapshot().Protocols,
            protocol => protocol.Protocol == "WPS-2.0.2");
        Assert.Equal(before + 1, after.RequestCount);
    }

    [Theory]
    [InlineData("/wps", "WPS-2.0.2")]
    [InlineData("/WPS/", "WPS-2.0.2")]
    [InlineData("/wps/conformance/results/example", "WPS-2.0.2")]
    [InlineData("/wps-other", null)]
    public void WpsClassifier_RespectsPathSegmentBoundaries(string path, string? expectedProtocol) =>
        Assert.Equal(expectedProtocol, RequestTelemetryClassifier.ResolveProtocol(new PathString(path)));

    private sealed record Sample(string Name, double Value, Dictionary<string, object?> Tags);

    private sealed class Metrics : IDisposable
    {
        private readonly MeterListener _listener = new();
        public ConcurrentQueue<Sample> Samples { get; } = new();

        public Metrics()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == HonuaTelemetry.ServiceName &&
                    instrument.Name is "honua_request_error_total" or "honua_serving_request_duration_ms")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                Samples.Enqueue(new Sample(instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value))));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                Samples.Enqueue(new Sample(instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value))));
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
