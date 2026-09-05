// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using Honua.Infrastructure.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Honua.Server.Tests.Infrastructure;

[Trait("Tier", "Fast")]
public sealed class RequestLogRedactionTests
{
    [Fact]
    public async Task ProductionDiagnostics_RedactsCredentialsBeforeSinksAndProviders()
    {
        var sink = new CaptureSink();
        var provider = new CaptureProvider();
        using var host = await new HostBuilder()
            .UseEnvironment("Production")
            .ConfigureLogging(logging => logging.AddProvider(provider))
            .UseSerilog((_, configuration) => configuration
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .ConfigureHonuaRequestDiagnostics()
                .WriteTo.Sink(sink), writeToProviders: true)
            .ConfigureWebHost(web => web.UseTestServer().Configure(app =>
            {
                app.UseSerilogRequestLogging(options =>
                    options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms");
                app.Run(context =>
                {
                    Assert.Equal("query-marker", context.Request.Query["token"]);
                    Assert.Equal("Bearer header-marker", context.Request.Headers.Authorization);
                    return context.Response.WriteAsync("ok");
                });
            }))
            .StartAsync();
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/rest/services/demo/FeatureServer/0/query?token=query-marker&where=email%3Dfilter-marker");
        request.Headers.Add("Authorization", "Bearer header-marker");
        request.Headers.Add("X-Api-Key", "key-marker");
        request.Headers.Add("Cookie", "session=cookie-marker");
        using var response = await client.SendAsync(request);
        Assert.Equal(200, (int)response.StatusCode);
        // Stop waits for the framework request-finished event before inspecting captures.
        await host.StopAsync();

        Assert.Contains(sink.Events, e => e.MessageTemplate.Text.StartsWith("Request starting", StringComparison.Ordinal));
        Assert.Contains(sink.Events, e => e.MessageTemplate.Text.StartsWith("Request finished", StringComparison.Ordinal));
        Assert.Contains(sink.Events, e => e.MessageTemplate.Text.StartsWith("HTTP", StringComparison.Ordinal));
        var rendered = string.Join('\n', sink.Events.Select(e => e.RenderMessage(CultureInfo.InvariantCulture) + e.Properties));
        var forwarded = string.Join('\n', provider.Messages);
        Assert.Contains("/rest/services/demo/FeatureServer/0/query", rendered, StringComparison.Ordinal);
        Assert.Contains("/rest/services/demo/FeatureServer/0/query", forwarded, StringComparison.Ordinal);
        foreach (var marker in new[] { "query-marker", "filter-marker", "header-marker", "key-marker", "cookie-marker" })
        {
            Assert.DoesNotContain(marker, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, forwarded, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("token")]
    [InlineData("ACCESS_TOKEN")]
    [InlineData("api%5Fkey")]
    [InlineData("password")]
    [InlineData("client_secret")]
    [InlineData("customSigningKey")]
    [InlineData("vendorToken")]
    [InlineData("vendorSecret")]
    [InlineData("sig")]
    public void StructuredProperties_RedactCredentialQueryParameters(string parameter)
    {
        var sink = new CaptureSink();
        using var logger = new LoggerConfiguration().ConfigureHonuaRequestDiagnostics().WriteTo.Sink(sink).CreateLogger();
        logger.Information("Request {Url} {CorrelationId}", $"https://example.test/query?f=json&{parameter}=synthetic-marker&count=1", "safe-correlation");
        var entry = Assert.Single(sink.Events);
        Assert.DoesNotContain("synthetic-marker", entry.RenderMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.Contains("safe-correlation", entry.RenderMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.Contains("f=json", entry.RenderMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("X-Esri-Authorization")]
    [InlineData("Proxy-Authorization")]
    [InlineData("X-Api-Key")]
    [InlineData("Cookie")]
    [InlineData("Set-Cookie")]
    [InlineData("CustomSecret")]
    public void StructuredProperties_RedactCredentialHeadersIncludingNestedValues(string header)
    {
        var sink = new CaptureSink();
        using var logger = new LoggerConfiguration().ConfigureHonuaRequestDiagnostics().WriteTo.Sink(sink).CreateLogger();
        logger.ForContext(header, "synthetic-marker")
            .ForContext("Details", new Dictionary<string, object> { [header] = new[] { "synthetic-marker" } }, destructureObjects: true)
            .Information("Safe request {CorrelationId}", "safe-correlation");
        var entry = Assert.Single(sink.Events);
        Assert.DoesNotContain("synthetic-marker", string.Join(' ', entry.Properties.Values), StringComparison.Ordinal);
        Assert.Contains("safe-correlation", entry.RenderMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private sealed class CaptureSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    private sealed class CaptureProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new CaptureLogger(Messages);
        public void Dispose() { }

        private sealed class CaptureLogger(ConcurrentQueue<string> messages) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
        }
    }
}
