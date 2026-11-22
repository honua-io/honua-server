// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Honua.Server.Tests.Startup;

[CollectionDefinition("ConsoleLogging", DisableParallelization = true)]
public sealed class ConsoleLoggingCollection
{
}

[Collection("ConsoleLogging")]
public sealed partial class StructuredLoggingConfigurationTests
{
    [UnitTheory]
    [InlineData(Environments.Production)]
    [InlineData(Environments.Staging)]
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

        Assert.Single(logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log)));
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
