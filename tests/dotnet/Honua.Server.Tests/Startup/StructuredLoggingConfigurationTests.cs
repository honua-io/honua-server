// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Honua.ServiceDefaults;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenTelemetry.Logs;

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

    private static WebApplicationBuilder CreateBuilder(string environment) =>
        WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            ContentRootPath = AppContext.BaseDirectory,
            Args = []
        });
}
