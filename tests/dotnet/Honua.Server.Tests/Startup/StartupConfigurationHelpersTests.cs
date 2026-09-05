// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Startup;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Honua.Server.Tests.Startup;

/// <summary>
/// Coverage for the pre-Build Redis connection-string secret-reference resolution
/// (<see cref="StartupConfigurationHelpers.ResolveRedisConnectionSecretReferencesAsync"/>,
/// honua-server#3011). The <c>aws:secretsmanager:</c> resolution mechanism itself — the part that
/// matters for the bug (a secret reference must come out the other side as a real connection
/// string before <c>ConfigurationOptions.Parse</c> sees it) — is covered without any network access
/// in <c>ConnectionStringResolutionHelperTests</c>, since this method delegates to
/// <c>ConnectionStringResolutionHelper.ResolveConnectionStringValueAsync</c> with a bootstrap-only
/// <c>AwsSecretsManagerResolver</c> instance. These tests instead prove the production entry
/// point's guard behavior: it must recognize non-reference values and return immediately without
/// constructing an HTTP client or touching the network.
/// </summary>
public sealed class StartupConfigurationHelpersTests
{
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void FinalSourceOrdering_ResolvesSecurityOverrideInsteadOfBaseSecret()
    {
        var directory = Path.Combine(Path.GetTempPath(), "honua-security-precedence-" + Guid.NewGuid().ToString("N"));
        var baseName = "HONUA_BASE_SECRET_" + Guid.NewGuid().ToString("N");
        var overrideName = "HONUA_OVERRIDE_SECRET_" + Guid.NewGuid().ToString("N");
        var expected = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(directory);
        try
        {
            Environment.SetEnvironmentVariable(baseName, Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(overrideName, expected);
            foreach (var (file, name) in new[] { ("appsettings.json", baseName), ("appsettings.Security.json", overrideName) })
            {
                File.WriteAllText(Path.Combine(directory, file),
                    $$"""{"HONUA_ADMIN_PASSWORD":"env:{{name}}","Security:ConnectionEncryption:MasterKey":"env:{{name}}","ConnectionStrings:Redis":"env:{{name}}"}""");
            }
            File.WriteAllText(Path.Combine(directory, "appsettings.Production.json"), "{}");
            using var configuration = new ConfigurationManager();
            configuration.SetBasePath(directory);
            configuration.AddJsonFile("appsettings.json");
            configuration.AddJsonFile("appsettings.Production.json");
            var environment = Substitute.For<IHostEnvironment>();
            environment.EnvironmentName.Returns(Environments.Production);

            StartupConfigurationHelpers.AddSecurityConfiguration(configuration, environment);
            StartupConfigurationHelpers.ResolveEnvironmentSecretReferences(configuration);

            foreach (var key in new[] { "HONUA_ADMIN_PASSWORD", "Security:ConnectionEncryption:MasterKey", "ConnectionStrings:Redis" })
            {
                string.Equals(configuration[key], expected, StringComparison.Ordinal).Should().BeTrue(key);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(baseName, null);
            Environment.SetEnvironmentVariable(overrideName, null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("ConnectionStrings:redis")]
    [InlineData("Aspire:StackExchange:Redis:ConnectionString")]
    public async Task SecuritySourceReordering_PreservesAwsResolvedSnapshot(string key)
    {
        var directory = Path.Combine(Path.GetTempPath(), "honua-aws-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "appsettings.json"), "{}");
            File.WriteAllText(Path.Combine(directory, "appsettings.Production.json"), "{}");
            const string reference = "aws:secretsmanager:regression/redis";
            var resolved = "localhost:6379,password=" + Guid.NewGuid().ToString("N");
            using var configuration = new ConfigurationManager();
            configuration.SetBasePath(directory);
            configuration.AddJsonFile("appsettings.json");
            configuration.AddJsonFile("appsettings.Production.json");
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = reference });
            var resolver = Substitute.For<IConnectionSecretResolver>();
            resolver.CanResolveSecretAsync(reference, Arg.Any<CancellationToken>()).Returns(true);
            resolver.ResolveConnectionStringAsync(reference, Arg.Any<CancellationToken>()).Returns(resolved);
            var environment = Substitute.For<IHostEnvironment>();
            environment.EnvironmentName.Returns(Environments.Production);

            await StartupConfigurationHelpers.ResolveRedisConnectionSecretReferenceAsync(configuration, key, resolver);
            StartupConfigurationHelpers.AddSecurityConfiguration(configuration, environment);

            string.Equals(configuration[key], resolved, StringComparison.Ordinal).Should().BeTrue();
            await resolver.Received(1).CanResolveSecretAsync(reference, Arg.Any<CancellationToken>());
            await resolver.Received(1).ResolveConnectionStringAsync(reference, Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("ConnectionStrings:redis", false)]
    [InlineData("ConnectionStrings:Redis", true)]
    [InlineData("Aspire:StackExchange:Redis:ConnectionString", false)]
    [InlineData("Aspire:StackExchange:Redis:ConnectionString", true)]
    public void SecuritySourceReordering_PreservesResolvedEnvironmentReferences(string key, bool includeSecurityFile)
    {
        var directory = Path.Combine(Path.GetTempPath(), "honua-secret-order-" + Guid.NewGuid().ToString("N"));
        var prefix = "HONUA_SECRET_ORDER_" + Guid.NewGuid().ToString("N") + "_";
        var referenceName = prefix + "VALUE";
        var configurationName = prefix + key.Replace(":", "__", StringComparison.Ordinal);
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "appsettings.json"), "{}");
            File.WriteAllText(Path.Combine(directory, "appsettings.Production.json"), "{}");
            if (includeSecurityFile)
            {
                File.WriteAllText(Path.Combine(directory, "appsettings.Security.json"), "{}");
            }
            var resolved = "localhost:6379,password=" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(referenceName, resolved);
            Environment.SetEnvironmentVariable(configurationName, "env:" + referenceName);
            using var configuration = new ConfigurationManager();
            configuration.SetBasePath(directory);
            configuration.AddJsonFile("appsettings.json");
            configuration.AddJsonFile("appsettings.Production.json");
            configuration.AddEnvironmentVariables(prefix);
            var environment = Substitute.For<IHostEnvironment>();
            environment.EnvironmentName.Returns(Environments.Production);

            StartupConfigurationHelpers.ResolveEnvironmentSecretReferences(configuration);
            StartupConfigurationHelpers.AddSecurityConfiguration(configuration, environment);

            // Boolean assertion keeps generated credentials out of failure output.
            string.Equals(configuration[key], resolved, StringComparison.Ordinal).Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(referenceName, null);
            Environment.SetEnvironmentVariable(configurationName, null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveRedisConnectionSecretReferencesAsync_PlainConnectionString_LeavesValueUnchanged()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:redis"] = "localhost:6379",
        });

        await StartupConfigurationHelpers.ResolveRedisConnectionSecretReferencesAsync(configuration);

        configuration["ConnectionStrings:redis"].Should().Be("localhost:6379");
    }

    [Fact]
    public async Task ResolveRedisConnectionSecretReferencesAsync_NotConfigured_NoOp()
    {
        using var configuration = new ConfigurationManager();

        await StartupConfigurationHelpers.ResolveRedisConnectionSecretReferencesAsync(configuration);

        configuration["ConnectionStrings:redis"].Should().BeNull();
        configuration["Aspire:StackExchange:Redis:ConnectionString"].Should().BeNull();
    }

    [Fact]
    public async Task ResolveRedisConnectionSecretReferencesAsync_AlreadyResolvedEnvironmentValue_LeavesValueUnchanged()
    {
        // env: references are normalized in place by ResolveEnvironmentSecretReferences before this
        // method ever runs (Program.cs calls it first), so by the time this method sees the value it
        // is already a plain connection string — prove it is left untouched here too, and that no
        // network call is attempted for it.
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:redis"] = "already-resolved-host:6379",
        });

        await StartupConfigurationHelpers.ResolveRedisConnectionSecretReferencesAsync(configuration);

        configuration["ConnectionStrings:redis"].Should().Be("already-resolved-host:6379");
    }

    [Fact]
    public async Task ResolveRedisConnectionSecretReferencesAsync_AspireConnectionStringConfigured_PlainValueLeftUnchanged()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Aspire:StackExchange:Redis:ConnectionString"] = "localhost:6379",
        });

        await StartupConfigurationHelpers.ResolveRedisConnectionSecretReferencesAsync(configuration);

        configuration["Aspire:StackExchange:Redis:ConnectionString"].Should().Be("localhost:6379");
    }

    [Fact]
    public async Task ResolveRedisConnectionSecretReferencesAsync_PrimaryConfigured_SkipsShadowedAspireReference()
    {
        const string shadowedReference = "aws:secretsmanager:stale/redis";
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:redis"] = "primary-host:6379",
            ["Aspire:StackExchange:Redis:ConnectionString"] = shadowedReference,
        });

        await StartupConfigurationHelpers.ResolveRedisConnectionSecretReferencesAsync(configuration);

        configuration["ConnectionStrings:redis"].Should().Be("primary-host:6379");
        configuration["Aspire:StackExchange:Redis:ConnectionString"].Should().Be(shadowedReference);
    }
}
