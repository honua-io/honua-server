// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.Configuration;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Configuration;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Features.Infrastructure.Configuration;

/// <summary>
/// The host's composed <see cref="ISecretProvider"/> treats <c>env://NAME</c> as a secret
/// reference (so callers such as the Studio AI transcript signer that gate on
/// <see cref="ISecretProvider.IsSecretReference"/> accept a CI-injected key) and resolves it
/// without the value ever reaching a log line.
/// </summary>
[Collection("Security")]
[SecurityTest]
public sealed class EnvSecretReferenceSecretProviderTests
{
    private const string VariableName = "HONUA_TEST_SECRET_PROVIDER_ENV_VALUE";
    private const string VariableValue = "secret-provider-env-value-91c2";
    private const string MissingVariableName = "HONUA_TEST_SECRET_PROVIDER_ENV_MISSING";

    [UnitTest]
    public void ComposedSecretProvider_SupportsEnvProvider_AndRecognizesBothReferenceForms()
    {
        using var services = BuildServices(new CapturingLoggerProvider());
        var provider = services.GetRequiredService<ISecretProvider>();

        provider.GetSupportedProviders().Should().Contain("env");
        provider.IsSecretReference("env://" + VariableName).Should().BeTrue();
        provider.IsSecretReference("env:" + VariableName).Should().BeTrue();
        provider.IsSecretReference("plain-inline-value").Should().BeFalse();
        provider.IsSecretReference("unknown://" + VariableName).Should().BeFalse();
    }

    [UnitTest]
    public async Task ComposedSecretProvider_ResolvesEnvUriReference_WithoutLoggingTheValue()
    {
        var previous = Environment.GetEnvironmentVariable(VariableName);
        Environment.SetEnvironmentVariable(VariableName, VariableValue);
        try
        {
            var logs = new CapturingLoggerProvider();
            using var services = BuildServices(logs);
            var provider = services.GetRequiredService<ISecretProvider>();

            (await provider.GetSecretAsync("env://" + VariableName)).Should().Be(VariableValue);
            (await provider.CanResolveSecretAsync("env://" + VariableName)).Should().BeTrue();

            logs.Messages.Should().NotBeEmpty("secret access logging is enabled for this host");
            logs.Messages.Should().NotContain(message => message.Contains(VariableValue, StringComparison.Ordinal));
            logs.Messages.Should().Contain(message => message.Contains("env:***", StringComparison.Ordinal),
                "secret references are logged through the existing provider-prefix mask");
        }
        finally
        {
            Environment.SetEnvironmentVariable(VariableName, previous);
        }
    }

    [UnitTest]
    public async Task ComposedSecretProvider_MissingEnvVariable_FailsTypedAndDefaultsOnlyWhenAsked()
    {
        var previous = Environment.GetEnvironmentVariable(MissingVariableName);
        Environment.SetEnvironmentVariable(MissingVariableName, null);
        try
        {
            var logs = new CapturingLoggerProvider();
            using var services = BuildServices(logs);
            var provider = services.GetRequiredService<ISecretProvider>();

            await FluentActions.Awaiting(() => provider.GetSecretAsync("env://" + MissingVariableName))
                .Should().ThrowAsync<SecretNotFoundException>();
            (await provider.GetSecretOrDefaultAsync("env://" + MissingVariableName)).Should().BeNull();
            (await provider.CanResolveSecretAsync("env://" + MissingVariableName)).Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(MissingVariableName, previous);
        }
    }

    private static ServiceProvider BuildServices(CapturingLoggerProvider logs)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SecretProvider:LogSecretAccess"] = "true",
                ["SecretProvider:EnableCaching"] = "false",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(logs));
        ConfigurationServiceExtensions.AddSecretManagement(services, configuration);
        return services.BuildServiceProvider();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => messages.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " | " + exception));
        }
    }
}
