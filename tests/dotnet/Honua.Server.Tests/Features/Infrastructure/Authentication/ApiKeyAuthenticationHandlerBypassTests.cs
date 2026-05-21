// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.Authentication;

/// <summary>
/// Unit tests for the tightened HONUA_DEV_AUTH bypass behaviour.
/// Audit #1144: the bypass must ONLY activate when ALL of
///   - ASPNETCORE_ENVIRONMENT is exactly "Test" (NOT Development, NOT Staging),
///   - HONUA_DEV_AUTH=true,
///   - HONUA_DEV_AUTH_ACK matches the expected acknowledgement string.
/// </summary>
public class ApiKeyAuthenticationHandlerBypassTests
{
    private const string ValidAck = ApiKeyAuthenticationOptions.ExpectedDevAuthBypassAck;

    // ---------------------------------------------------------------------
    // Per-request handler activation tests
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Handler_Test_True_WithAck_ActivatesBypass()
    {
        var options = new ApiKeyAuthenticationOptions
        {
            EnvironmentName = "Test",
            DevAuthBypass = "true",
            DevAuthBypassAck = ValidAck,
            IsTestMode = true,
        };

        var result = await AuthenticateAsync(options);

        Assert.True(result.Succeeded);
        Assert.Equal("dev-bypass", result.Principal!.FindFirst("auth_type")?.Value);
    }

    [Fact]
    public async Task Handler_Development_True_WithAck_DoesNotActivateBypass()
    {
        // Even with the ack, the wrong environment must not activate the bypass.
        var options = new ApiKeyAuthenticationOptions
        {
            EnvironmentName = "Development",
            DevAuthBypass = "true",
            DevAuthBypassAck = ValidAck,
            IsDevelopmentMode = true,
        };

        var result = await AuthenticateAsync(options);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Handler_Staging_True_WithAck_DoesNotActivateBypass()
    {
        // Specifically guards against the original audit finding: a typoed
        // ASPNETCORE_ENVIRONMENT=Development (or Staging) on a staging deploy.
        var options = new ApiKeyAuthenticationOptions
        {
            EnvironmentName = "Staging",
            DevAuthBypass = "true",
            DevAuthBypassAck = ValidAck,
        };

        var result = await AuthenticateAsync(options);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Handler_Production_True_WithAck_DoesNotActivateBypass()
    {
        var options = new ApiKeyAuthenticationOptions
        {
            EnvironmentName = "Production",
            DevAuthBypass = "true",
            DevAuthBypassAck = ValidAck,
        };

        var result = await AuthenticateAsync(options);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Handler_Test_True_MissingAck_DoesNotActivateBypass()
    {
        var options = new ApiKeyAuthenticationOptions
        {
            EnvironmentName = "Test",
            DevAuthBypass = "true",
            DevAuthBypassAck = null,
            IsTestMode = true,
        };

        var result = await AuthenticateAsync(options);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Handler_Test_True_WrongAck_DoesNotActivateBypass()
    {
        var options = new ApiKeyAuthenticationOptions
        {
            EnvironmentName = "Test",
            DevAuthBypass = "true",
            DevAuthBypassAck = "yes-please",
            IsTestMode = true,
        };

        var result = await AuthenticateAsync(options);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Handler_Test_DevAuthFalse_WithAck_DoesNotActivateBypass()
    {
        var options = new ApiKeyAuthenticationOptions
        {
            EnvironmentName = "Test",
            DevAuthBypass = "false",
            DevAuthBypassAck = ValidAck,
            IsTestMode = true,
        };

        var result = await AuthenticateAsync(options);

        Assert.False(result.Succeeded);
    }

    // ---------------------------------------------------------------------
    // Startup validator tests
    // ---------------------------------------------------------------------

    [Fact]
    public void Validator_Production_True_Throws()
    {
        var logger = new CapturingLoggerFactory();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DevAuthBypassStartupValidator.Validate(
                environmentName: "Production",
                devAuthBypass: "true",
                devAuthBypassAck: ValidAck,
                loggerFactory: logger));

        Assert.Contains("Production", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Error
                 && e.Message.Contains("Production", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_Production_True_WithoutAck_StillThrows()
    {
        var logger = new CapturingLoggerFactory();

        Assert.Throws<InvalidOperationException>(() =>
            DevAuthBypassStartupValidator.Validate(
                environmentName: "Production",
                devAuthBypass: "true",
                devAuthBypassAck: null,
                loggerFactory: logger));
    }

    [Fact]
    public void Validator_Development_True_WithAck_LogsWarning()
    {
        var logger = new CapturingLoggerFactory();

        DevAuthBypassStartupValidator.Validate(
            environmentName: "Development",
            devAuthBypass: "true",
            devAuthBypassAck: ValidAck,
            loggerFactory: logger);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("HONUA_DEV_AUTH", warning.Message, StringComparison.Ordinal);
        Assert.Contains("not active", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Test", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_Test_True_MissingAck_LogsWarning()
    {
        var logger = new CapturingLoggerFactory();

        DevAuthBypassStartupValidator.Validate(
            environmentName: "Test",
            devAuthBypass: "true",
            devAuthBypassAck: null,
            loggerFactory: logger);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("HONUA_DEV_AUTH_ACK", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_Test_True_WithAck_NoWarnings()
    {
        var logger = new CapturingLoggerFactory();

        DevAuthBypassStartupValidator.Validate(
            environmentName: "Test",
            devAuthBypass: "true",
            devAuthBypassAck: ValidAck,
            loggerFactory: logger);

        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void Validator_DevAuthNotSet_IsSilent()
    {
        var logger = new CapturingLoggerFactory();

        DevAuthBypassStartupValidator.Validate(
            environmentName: "Production",
            devAuthBypass: null,
            devAuthBypassAck: null,
            loggerFactory: logger);

        Assert.Empty(logger.Entries);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static async Task<AuthenticateResult> AuthenticateAsync(ApiKeyAuthenticationOptions authOptions)
    {
        var dependencies = new ApiKeyAuthenticationDependencies(Options.Create(authOptions));
        var handler = new ApiKeyAuthenticationHandler(
            new TestSchemeOptionsMonitor(),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            dependencies);

        var scheme = new AuthenticationScheme(
            AuthenticationExtensions.ApiKeyScheme,
            displayName: null,
            handlerType: typeof(ApiKeyAuthenticationHandler));

        var context = new DefaultHttpContext();
        await handler.InitializeAsync(scheme, context);
        return await handler.AuthenticateAsync();
    }

    private sealed class TestSchemeOptionsMonitor : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        private readonly AuthenticationSchemeOptions _options = new();

        public AuthenticationSchemeOptions CurrentValue => _options;

        public AuthenticationSchemeOptions Get(string? name) => _options;

        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<LogEntry> Entries { get; } = [];

        public void AddProvider(ILoggerProvider provider) { }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose() { }
    }

    private sealed class CapturingLogger(List<LogEntry> sink) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            sink.Add(new LogEntry(logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
