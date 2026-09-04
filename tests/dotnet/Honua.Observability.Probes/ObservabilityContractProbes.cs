using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Honua.Observability.Probes;

public sealed class ObservabilityContractProbes
{
    [Fact]
    public async Task HostingDiagnostics_MustNotLogQueryCredentials()
    {
        var sink = new CaptureSink();
        // Production category levels from Program.cs. Exercise the real ASP.NET
        // hosting logger, which runs outside Serilog's path-only request middleware.
        using var host = await new HostBuilder()
            .UseSerilog((_, configuration) => configuration.MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", LogEventLevel.Information)
                .WriteTo.Sink(sink))
            .ConfigureWebHost(web => web.UseTestServer().Configure(app =>
                app.Run(context => context.Response.WriteAsync("ok"))))
            .StartAsync();
        using var client = host.GetTestClient();
        using var response = await client.GetAsync("/rest/services/demo/FeatureServer/0/query?token=probe-token-marker&where=email%3Dprobe-email-marker");
        var events = sink.Events.Where(e => e.Level == LogEventLevel.Information).ToArray();
        Assert.NotEmpty(events);
        Assert.DoesNotContain(events, e => e.RenderMessage().Contains("probe-token-marker", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.RenderMessage().Contains("probe-email-marker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GeoServicesHttp200Error_MustHaveInBandTrue()
    {
        var captured = new List<Dictionary<string, object?>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == "honua_request_error_total")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => captured.Add(tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
        listener.Start();
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/rest/services/demo/FeatureServer/0/query";
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await StandardErrorResponseFormatter.WriteErrorAsync(context, StandardErrorResponse.BadRequest("Invalid where clause."));
        Assert.Equal(200, context.Response.StatusCode);
        var measurement = Assert.Single(captured);
        Assert.Equal(400, measurement["error_code"]);
        Assert.Equal(true, measurement["in_band"]);
    }

    [Theory]
    [InlineData("/wms")]
    [InlineData("/wps")]
    public void GaClassicRoutes_MustHaveServingProtocol(string path)
    {
        Assert.False(string.IsNullOrEmpty(RequestTelemetryClassifier.ResolveProtocol(new PathString(path))),
            $"GA serving route {path} is silently excluded from serving latency and its denominator");
    }

    private sealed class CaptureSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}
