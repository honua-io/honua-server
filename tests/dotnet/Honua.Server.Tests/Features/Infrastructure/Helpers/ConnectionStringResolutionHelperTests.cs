// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Infrastructure.Helpers;

public sealed class ConnectionStringResolutionHelperTests
{
    [Fact]
    public async Task ResolveDefaultConnectionStringAsync_WhenSecretResolverCanResolve_ReturnsResolvedValue()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "aws:secretsmanager:test-db"
            })
            .Build();
        var resolver = new StubConnectionSecretResolver("aws:secretsmanager:test-db", "Host=resolved;Database=honua;Username=test;Password=secret");

        var connectionString = await ConnectionStringResolutionHelper.ResolveDefaultConnectionStringAsync(configuration, resolver);

        connectionString.Should().Be("Host=resolved;Database=honua;Username=test;Password=secret");
    }

    [Fact]
    public async Task ResolveDefaultConnectionStringAsync_WhenResolverCannotResolve_ReturnsOriginalValue()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test;Username=test;Password=test"
            })
            .Build();
        var resolver = new StubConnectionSecretResolver("aws:secretsmanager:test-db", "Host=resolved;Database=honua;Username=test;Password=secret");

        var connectionString = await ConnectionStringResolutionHelper.ResolveDefaultConnectionStringAsync(configuration, resolver);

        connectionString.Should().Be("Host=localhost;Database=test;Username=test;Password=test");
    }

    // ResolveConnectionStringValueAsync is the generalized mechanism ResolveDefaultConnectionStringAsync
    // now delegates to; Program.cs's bootstrap Redis wiring (honua-server#3011) reuses it directly for
    // ConnectionStrings:redis since it runs before WebApplicationBuilder.Build(), when no DI-registered
    // IConnectionSecretResolver exists yet. These tests exercise the exact same resolution order the
    // Postgres DefaultConnection path uses, with a fake resolver — no real AWS calls.
    [Fact]
    public async Task ResolveConnectionStringValueAsync_AwsSecretsManagerReference_ResolvesThroughResolverChain()
    {
        var resolver = new StubConnectionSecretResolver(
            "aws:secretsmanager:prod/redis",
            "redis-host.internal:6379,password=resolved-secret");

        var resolved = await ConnectionStringResolutionHelper.ResolveConnectionStringValueAsync(
            "aws:secretsmanager:prod/redis",
            "ConnectionStrings:redis",
            resolver);

        resolved.Should().Be("redis-host.internal:6379,password=resolved-secret");
    }

    [Fact]
    public async Task ResolveConnectionStringValueAsync_EnvironmentReference_ResolvesFromEnvironmentVariable()
    {
        const string variableName = "HONUA_TEST_REDIS_CONNECTION_STRING_3011";
        Environment.SetEnvironmentVariable(variableName, "localhost:6380,password=env-secret");
        try
        {
            var resolved = await ConnectionStringResolutionHelper.ResolveConnectionStringValueAsync(
                $"env:{variableName}",
                "ConnectionStrings:redis",
                secretResolver: null);

            resolved.Should().Be("localhost:6380,password=env-secret");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public async Task ResolveConnectionStringValueAsync_PlainConnectionString_PassesThroughUnchanged()
    {
        // A resolver is present but must never be consulted for a value it can't recognize as a
        // reference — proves a plain connection string is left untouched.
        var resolver = new StubConnectionSecretResolver("aws:secretsmanager:prod/redis", "should-not-be-used");

        var resolved = await ConnectionStringResolutionHelper.ResolveConnectionStringValueAsync(
            "localhost:6379",
            "ConnectionStrings:redis",
            resolver);

        resolved.Should().Be("localhost:6379");
    }

    private sealed class StubConnectionSecretResolver(string secretRef, string resolvedConnectionString) : IConnectionSecretResolver
    {
        public string ProviderName => "aws";

        public Task<string?> ResolveSecretAsync(string candidate, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(candidate == secretRef ? resolvedConnectionString : null);

        public bool CanResolve(string candidate)
            => candidate == secretRef;

        public Task<string> ResolveConnectionStringAsync(string candidate, CancellationToken cancellationToken = default)
            => Task.FromResult(candidate == secretRef ? resolvedConnectionString : candidate);
    }
}
