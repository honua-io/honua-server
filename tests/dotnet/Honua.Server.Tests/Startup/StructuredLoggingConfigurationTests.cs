// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.MultiTenancy;
using Honua.ServiceDefaults;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenTelemetry.Logs;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Honua.Server.Tests.Startup;

[CollectionDefinition("ConsoleLogging", DisableParallelization = true)]
public sealed class ConsoleLoggingFixture
{
}

[Collection("ConsoleLogging")]
public sealed partial class StructuredLoggingConfigurationTests
{
    [UnitTheory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task StructuredConsole_PreservesMultilineValuesInOneJsonEvent(string environment)
    {
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var originalOutput = Console.Out;
        Console.SetOut(output);
        try
        {
            var builder = CreateBuilder(environment);
            builder.ConfigureStructuredLogging();
            await using (var app = builder.Build())
            {
                var logger = app.Services.GetRequiredService<ILogger<StructuredLoggingConfigurationTests>>();
                LogProcessing(logger, "alpha\r\nbeta", new InvalidOperationException("first\r\nsecond"));
            }

            var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            using var entry = JsonDocument.Parse(Assert.Single(lines));
            Assert.Equal("alpha\r\nbeta", entry.RootElement.GetProperty("Value").GetString());
            Assert.Contains("first\r\nsecond", entry.RootElement.GetProperty("@x").GetString());
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    [UnitTest]
    public async Task StructuredLogging_ForwardsEventsToLaterProviders()
    {
        var builder = CreateBuilder(Environments.Development);
        builder.ConfigureStructuredLogging();
        var provider = Substitute.For<ILoggerProvider>();
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        provider.CreateLogger(Arg.Any<string>()).Returns(logger);
        builder.Logging.AddProvider(provider);
        await using var app = builder.Build();

        LogForwarding(app.Services.GetRequiredService<ILogger<StructuredLoggingConfigurationTests>>());

        Assert.Single(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ILogger.Log));
    }

    [UnitTest]
    public async Task StructuredLogging_ExportsLogsOverOtlpHttpProtobuf()
    {
        var payloads = new ConcurrentQueue<byte[]>();
        await using var receiver = await StartOtlpReceiverAsync(payloads);
        var builder = CreateBuilder(Environments.Production);
        builder.ConfigureStructuredLogging();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = receiver.Urls.Single() + "/v1/logs",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
        });
        builder.Services.AddHttpClient();
        builder.AddTelemetryDefaults();

        await using (var app = builder.Build())
        {
            LogForwarding(app.Services.GetRequiredService<ILogger<StructuredLoggingConfigurationTests>>());
            Assert.True(app.Services.GetRequiredService<LoggerProvider>().ForceFlush(10_000));
        }

        Assert.Contains(payloads, payload => Encoding.UTF8.GetString(payload)
            .Contains("Provider forwarding remains enabled", StringComparison.Ordinal));
    }

    [UnitTest]
    public void ConfigureLogLevels_BindsDefaultAndCategoryOverridesFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Warning",
                ["Logging:LogLevel:Honua.Custom.Debug"] = "Debug",
                ["Logging:LogLevel:Microsoft"] = "Information",
                ["Logging:LogLevel:Honua.Muted"] = "None",
                ["Logging:LogLevel:Honua.Invalid"] = "NotALevel",
            })
            .Build();

        var events = new List<LogEvent>();
        var loggerConfig = new LoggerConfiguration()
            .WriteTo.Sink(new ListLogEventSink(events));

        StructuredLoggingConfiguration.ConfigureLogLevels(configuration, loggerConfig);
        using var logger = loggerConfig.CreateLogger();

        logger.ForContext(Constants.SourceContextPropertyName, "Honua.Default").Information("suppressed-info");
        logger.ForContext(Constants.SourceContextPropertyName, "Honua.Default").Warning("emitted-warning");
        logger.ForContext(Constants.SourceContextPropertyName, "Honua.Custom.Debug").Debug("emitted-debug");
        logger.ForContext(Constants.SourceContextPropertyName, "Microsoft.Custom").Information("emitted-ms-info");
        logger.ForContext(Constants.SourceContextPropertyName, "Honua.Muted").Fatal("suppressed-fatal");

        var messages = events.Select(e => e.MessageTemplate.Text).ToArray();
        Assert.DoesNotContain("suppressed-info", messages);
        Assert.Contains("emitted-warning", messages);
        Assert.Contains("emitted-debug", messages);
        Assert.Contains("emitted-ms-info", messages);
        Assert.DoesNotContain("suppressed-fatal", messages);
    }

    [UnitTest]
    public async Task StructuredLogging_TenantContextMiddleware_PushesTenantIdIntoLogContextAndActivityAndDisposesOnExit()
    {
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var originalOutput = Console.Out;
        Console.SetOut(output);
        try
        {
            string? capturedTag = null;
            await using var app = BuildTenantCorrelationApp(tag => capturedTag = tag);
            await app.StartAsync();
            var response = await app.GetTestClient().GetAsync("/tenant-log-check");
            response.EnsureSuccessStatusCode();
            await app.StopAsync();

            Assert.Equal("tenant-corr-42", capturedTag);
            AssertTenantLogOutput(output.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    private static WebApplication BuildTenantCorrelationApp(Action<string?> onActivityTag)
    {
        var builder = CreateBuilder(Environments.Production);
        builder.WebHost.UseTestServer();
        builder.ConfigureStructuredLogging();
        builder.Services.AddHonuaTenantContext(builder.Configuration, opt => opt.Enabled = true);

        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            using var requestActivity = new Activity("test-tenant-request");
            requestActivity.Start();
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "user-1"), new Claim("tid", "tenant-corr-42")],
                "TestAuth"));
            await next();
            onActivityTag(requestActivity.GetTagItem(HonuaTelemetry.Tags.TenantId) as string);
            LogTenantBoundary(
                ctx.RequestServices.GetRequiredService<ILogger<StructuredLoggingConfigurationTests>>(),
                "after-exit");
        });

        app.UseHonuaTenantContext();
        app.MapGet("/tenant-log-check", (HttpContext ctx) =>
        {
            LogTenantBoundary(
                ctx.RequestServices.GetRequiredService<ILogger<StructuredLoggingConfigurationTests>>(),
                "inside-request");
            return Results.Ok();
        });
        return app;
    }

    private static void AssertTenantLogOutput(string consoleOutput)
    {
        var entries = consoleOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line))
            .ToList();
        try
        {
            var inside = entries.Single(doc =>
                doc.RootElement.TryGetProperty("Phase", out var p) && p.GetString() == "inside-request");
            Assert.Equal("tenant-corr-42", inside.RootElement.GetProperty("TenantId").GetString());

            var afterExit = entries.Single(doc =>
                doc.RootElement.TryGetProperty("Phase", out var p) && p.GetString() == "after-exit");
            Assert.False(afterExit.RootElement.TryGetProperty("TenantId", out _));
        }
        finally
        {
            foreach (var doc in entries)
            {
                doc.Dispose();
            }
        }
    }

    private static async Task<WebApplication> StartOtlpReceiverAsync(ConcurrentQueue<byte[]> payloads)
    {
        var receiverBuilder = WebApplication.CreateSlimBuilder();
        receiverBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        var receiver = receiverBuilder.Build();
        receiver.MapPost("/v1/logs", async (HttpContext context) =>
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer);
            payloads.Enqueue(buffer.ToArray());
            context.Response.ContentType = "application/x-protobuf";
        });
        await receiver.StartAsync();
        return receiver;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Processing {Value}")]
    private static partial void LogProcessing(ILogger logger, string value, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Provider forwarding remains enabled")]
    private static partial void LogForwarding(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Tenant correlation check {Phase}")]
    private static partial void LogTenantBoundary(ILogger logger, string phase);

    private sealed class ListLogEventSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }

    private static WebApplicationBuilder CreateBuilder(string environment) =>
        WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            ContentRootPath = AppContext.BaseDirectory,
            Args = []
        });
}
