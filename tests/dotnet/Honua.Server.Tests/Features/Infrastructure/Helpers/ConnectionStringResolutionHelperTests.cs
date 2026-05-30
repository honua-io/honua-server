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
