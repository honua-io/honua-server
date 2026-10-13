// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Configuration;
using Honua.Db.Postgres.Features.Security.ConnectionSecretResolvers;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.Security;

/// <summary>
/// The <c>env</c> secret provider resolves both the short <c>env:NAME</c> form and the URI-style
/// <c>env://NAME</c> form a CI harness injects (for example the terminal-model canary's
/// <c>env://HONUA_CANARY_TRANSCRIPT_SIGNING_SEED</c>), and fails typed without echoing values.
/// </summary>
[Collection("Security")]
[SecurityTest]
public sealed class EnvironmentSecretResolverTests
{
    private const string VariableName = "HONUA_TEST_ENV_SECRET_RESOLVER_VALUE";
    private const string VariableValue = "env-resolver-value-7f3a";
    private const string MissingVariableName = "HONUA_TEST_ENV_SECRET_RESOLVER_MISSING";

    [UnitTheory]
    [InlineData("env:" + VariableName)]
    [InlineData("env://" + VariableName)]
    [InlineData("ENV://" + VariableName)]
    public async Task ResolveSecretAsync_BothReferenceForms_ResolveTheVariable(string reference)
    {
        using var scope = new EnvironmentVariableScope(VariableName, VariableValue);
        var resolver = new EnvironmentSecretResolver();

        resolver.CanResolve(reference).Should().BeTrue();
        (await resolver.ResolveSecretAsync(reference)).Should().Be(VariableValue);
    }

    [UnitTheory]
    [InlineData("env:" + MissingVariableName)]
    [InlineData("env://" + MissingVariableName)]
    public async Task ResolveSecretAsync_MissingVariable_ThrowsTypedFailure(string reference)
    {
        using var scope = new EnvironmentVariableScope(MissingVariableName, null);
        var resolver = new EnvironmentSecretResolver();

        resolver.CanResolve(reference).Should().BeFalse();
        var failure = await FluentActions.Awaiting(() => resolver.ResolveSecretAsync(reference))
            .Should().ThrowAsync<SecretNotFoundException>();
        failure.Which.SecretReference.Should().Be(reference);
        failure.Which.Message.Should().Contain(MissingVariableName).And.NotContain("//");
    }

    [UnitTest]
    public async Task ResolveSecretAsync_EmptyVariable_ThrowsTypedFailure()
    {
        using var scope = new EnvironmentVariableScope(MissingVariableName, "   ");
        var resolver = new EnvironmentSecretResolver();

        await FluentActions.Awaiting(() => resolver.ResolveSecretAsync("env://" + MissingVariableName))
            .Should().ThrowAsync<SecretNotFoundException>();
    }

    [UnitTheory]
    [InlineData("env:")]
    [InlineData("env://")]
    [InlineData("env:///" + VariableName)]
    [InlineData("env://A B")]
    [InlineData("env://A=B")]
    [InlineData("aws:secretsmanager:" + VariableName)]
    public async Task ResolveSecretAsync_MalformedReference_IsRejected(string reference)
    {
        using var scope = new EnvironmentVariableScope(VariableName, VariableValue);
        var resolver = new EnvironmentSecretResolver();

        resolver.CanResolve(reference).Should().BeFalse();
        var failure = await FluentActions.Awaiting(() => resolver.ResolveSecretAsync(reference))
            .Should().ThrowAsync<ArgumentException>();
        failure.Which.Message.Should().NotContain(VariableValue);
    }

    [UnitTest]
    public async Task CompositeResolver_UriFormReference_IsSupportedAndNeverEchoesValueOnFailure()
    {
        using var present = new EnvironmentVariableScope(VariableName, VariableValue);
        using var missing = new EnvironmentVariableScope(MissingVariableName, null);
        var composite = new CompositeSecretResolver(
            [new EnvironmentSecretResolver(), new NullSecretResolver()],
            NullLogger<CompositeSecretResolver>.Instance);

        composite.GetSupportedProviders().Should().Contain("env");
        (await composite.ResolveConnectionStringAsync("env://" + VariableName)).Should().Be(VariableValue);

        var failure = await FluentActions
            .Awaiting(() => composite.ResolveConnectionStringAsync("env://" + MissingVariableName))
            .Should().ThrowAsync<SecretNotFoundException>();
        failure.Which.ToString().Should().NotContain(VariableValue);
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
