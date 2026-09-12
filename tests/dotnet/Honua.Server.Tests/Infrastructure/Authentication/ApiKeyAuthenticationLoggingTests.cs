// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Encodings.Web;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Authentication;

public sealed class ApiKeyAuthenticationLoggingTests
{
    private const string AdminPassword = "LoggingRegression-AdminPassword123!";

    [Theory]
    [InlineData(null, true)]
    [InlineData("false", true)]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("true", false)]
    [InlineData("TRUE", false)]
    [Trait("Tier", "Fast")]
    public async Task AuthenticateAsync_Production_WarnsOnlyForExplicitBypassConfiguration(
        string? bypass, bool validKey)
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);
        var schemes = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        schemes.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        var dependencies = new ApiKeyAuthenticationDependencies(Options.Create(new ApiKeyAuthenticationOptions
        {
            EnvironmentName = "Production",
            AdminPassword = AdminPassword,
            DevAuthBypass = bypass,
            DevAuthBypassAcknowledged = "true",
            // Even incorrectly supplied test-mode flags must never enable
            // bypass in Production.
            IsTestMode = true
        }));
        var handler = new ApiKeyAuthenticationHandler(schemes, loggerFactory, UrlEncoder.Default, dependencies);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-API-Key"] = validKey ? AdminPassword : "invalid-key";
        await handler.InitializeAsync(
            new AuthenticationScheme("ApiKey", null, typeof(ApiKeyAuthenticationHandler)), context);

        var result = await handler.AuthenticateAsync();

        Assert.Equal(validKey, result.Succeeded);
        if (!validKey)
        {
            Assert.NotNull(result.Failure);
            Assert.Equal("Invalid API key", result.Failure.Message);
        }

        var bypassWarnings = logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log))
            .Select(call => call.GetArguments())
            .Where(arguments => arguments[1] is EventId { Id: 4111 })
            .ToArray();
        if (string.Equals(bypass, "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(LogLevel.Warning, Assert.Single(bypassWarnings)[0]);
        }
        else
        {
            Assert.Empty(bypassWarnings);
        }
    }
}
