// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Security.ConnectionSecretResolvers;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.Security;

[Collection("Security")]
public sealed class CompositeSecretResolverTests
{
    private const string VariableName = "HONUA_TEST_COMPOSITE_RESOLVER_VALUE";
    private const string VariableValue = "composite-resolver-value";

    [SecurityTest]
    [Fact]
    public async Task ResolveConnectionStringAsync_WholeStringReference_ResolvesValue()
    {
        using var scope = new EnvironmentVariableScope(VariableName, VariableValue);
        var resolver = CreateResolver();

        var resolved = await resolver.ResolveConnectionStringAsync($"env:{VariableName}");

        resolved.Should().Be(VariableValue);
    }

    [SecurityTest]
    [Theory]
    [InlineData("Host=db.example.com;Password={env:" + VariableName + "}")]
    [InlineData("Host=db.example.com;Password=${env:" + VariableName + "}")]
    [InlineData("prefix {env:" + VariableName + "} suffix")]
    public async Task ResolveConnectionStringAsync_TextWithEmbeddedPlaceholder_IsReturnedUnchanged(string input)
    {
        using var scope = new EnvironmentVariableScope(VariableName, VariableValue);
        var resolver = CreateResolver();

        var resolved = await resolver.ResolveConnectionStringAsync(input);

        resolved.Should().Be(input);
        resolved.Should().NotContain(VariableValue);
    }

    private static CompositeSecretResolver CreateResolver()
        => new(
            [new EnvironmentSecretResolver(), new NullSecretResolver()],
            NullLogger<CompositeSecretResolver>.Instance);

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableScope(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
