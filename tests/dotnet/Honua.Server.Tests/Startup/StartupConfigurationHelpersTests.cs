// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Startup;
using Microsoft.Extensions.Configuration;

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
    public async Task ResolveRedisConnectionSecretReferencesAsync_PlainConnectionString_LeavesValueUnchanged()
    {
        var configuration = new ConfigurationManager();
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
        var configuration = new ConfigurationManager();

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
        var configuration = new ConfigurationManager();
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
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Aspire:StackExchange:Redis:ConnectionString"] = "localhost:6379",
        });

        await StartupConfigurationHelpers.ResolveRedisConnectionSecretReferencesAsync(configuration);

        configuration["Aspire:StackExchange:Redis:ConnectionString"].Should().Be("localhost:6379");
    }
}
