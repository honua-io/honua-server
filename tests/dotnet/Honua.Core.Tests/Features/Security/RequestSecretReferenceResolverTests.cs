// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Security;

/// <summary>
/// Unit tests for the operator policy that governs request-supplied secret references.
/// </summary>
public sealed class RequestSecretReferenceResolverTests
{
    private const string SecretValue = "resolved-value";

    [UnitTest]
    public async Task ResolveAsync_WithNoPolicyConfigured_RefusesEveryReference()
    {
        var inner = new RecordingSecretResolver();
        var resolver = Create(new RequestSecretReferenceOptions(), inner);

        resolver.Evaluate("env:IMPORT_TOKEN").IsPermitted.Should().BeFalse();
        resolver.Evaluate("aws:secretsmanager:imports/token").IsPermitted.Should().BeFalse();
        await FluentActions.Awaiting(() => resolver.ResolveAsync("env:IMPORT_TOKEN"))
            .Should().ThrowAsync<RequestSecretReferenceException>();
        inner.Requested.Should().BeEmpty();
    }

    [UnitTest]
    public async Task ResolveAsync_WithExactEnvironmentVariable_ResolvesOnlyThatName()
    {
        var inner = new RecordingSecretResolver();
        var resolver = Create(
            new RequestSecretReferenceOptions { AllowedEnvironmentVariables = ["IMPORT_TOKEN"] },
            inner);

        (await resolver.ResolveAsync("env:IMPORT_TOKEN")).Should().Be(SecretValue);
        (await resolver.ResolveAsync("ENV:IMPORT_TOKEN")).Should().Be(SecretValue);
        resolver.Evaluate("env:IMPORT_TOKEN_OTHER").IsPermitted.Should().BeFalse();
        resolver.Evaluate("env:import_token").IsPermitted.Should().BeFalse();
        inner.Requested.Should().Equal("env:IMPORT_TOKEN", "env:IMPORT_TOKEN");
    }

    [UnitTest]
    public void Evaluate_WithEnvironmentPrefix_DoesNotMatchConfigurationBindingNames()
    {
        var resolver = Create(new RequestSecretReferenceOptions
        {
            AllowedEnvironmentVariablePrefixes = ["IMPORT_", "Security"],
            AllowedEnvironmentVariables = ["Imports__SharedToken"]
        });

        resolver.Evaluate("env:IMPORT_ARCGIS_TOKEN").IsPermitted.Should().BeTrue();
        resolver.Evaluate("env:OTHER_TOKEN").IsPermitted.Should().BeFalse();
        resolver.Evaluate("env:Security__ConnectionEncryption__MasterKey").IsPermitted.Should().BeFalse();
        resolver.Evaluate("env:Imports__SharedToken").IsPermitted.Should().BeTrue();
    }

    [UnitTest]
    public void Evaluate_WithSecretReferencePrefix_MatchesProviderAndIdentifierPrefix()
    {
        var resolver = Create(new RequestSecretReferenceOptions
        {
            AllowedSecretReferencePrefixes = ["aws:secretsmanager:honua/imports/", "azure:keyvault:honua-imports:"]
        });

        resolver.Evaluate("aws:secretsmanager:honua/imports/arcgis").IsPermitted.Should().BeTrue();
        resolver.Evaluate("AWS:secretsmanager:honua/imports/arcgis").IsPermitted.Should().BeTrue();
        resolver.Evaluate("aws:secretsmanager:honua/platform/database").IsPermitted.Should().BeFalse();
        resolver.Evaluate("azure:keyvault:honua-imports:geoserver").IsPermitted.Should().BeTrue();
        resolver.Evaluate("azure:keyvault:honua-platform:database").IsPermitted.Should().BeFalse();
        resolver.Evaluate("vault:honua/imports/arcgis").IsPermitted.Should().BeFalse("the provider is not registered");
    }

    [UnitTest]
    public void Evaluate_EnvironmentEntriesDoNotPermitOtherProviders()
    {
        var resolver = Create(new RequestSecretReferenceOptions { AllowedEnvironmentVariables = ["IMPORT_TOKEN"] });

        resolver.Evaluate("aws:secretsmanager:IMPORT_TOKEN").IsPermitted.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("IMPORT_TOKEN")]
    [InlineData("env:")]
    [InlineData(":IMPORT_TOKEN")]
    [InlineData(" env:IMPORT_TOKEN")]
    [InlineData("env:IMPORT_TOKEN ")]
    [InlineData("env:IMPORT TOKEN")]
    [InlineData("env:IMPORT-TOKEN")]
    [InlineData("{env:IMPORT_TOKEN}")]
    [InlineData("${env:IMPORT_TOKEN}")]
    [InlineData("Host=db.example.com;Password={env:IMPORT_TOKEN}")]
    [InlineData("Host=db.example.com;Port=5432;Username=app;Password=inline")]
    [InlineData("aws:secretsmanager:honua/imports/{env:IMPORT_TOKEN}")]
    [InlineData("aws:secretsmanager:honua/imports/a;Host=db.example.com")]
    public async Task Evaluate_WithValueOutsideTheWholeStringGrammar_IsRefusedWithoutResolution(string? reference)
    {
        var inner = new RecordingSecretResolver();
        var resolver = Create(
            new RequestSecretReferenceOptions
            {
                AllowedEnvironmentVariables = ["IMPORT_TOKEN"],
                AllowedEnvironmentVariablePrefixes = ["IMPORT"],
                AllowedSecretReferencePrefixes = ["aws:secretsmanager:honua/imports/"]
            },
            inner);

        resolver.Evaluate(reference).IsPermitted.Should().BeFalse();
        await FluentActions.Awaiting(() => resolver.ResolveAsync(reference!))
            .Should().ThrowAsync<RequestSecretReferenceException>();
        inner.Requested.Should().BeEmpty();
    }

    [UnitTest]
    public async Task ResolveAsync_WhenProviderFails_UsesTheSameMessageAsARefusal()
    {
        var inner = new RecordingSecretResolver
        {
            Failure = new InvalidOperationException("Environment variable 'IMPORT_TOKEN' is not set or is empty")
        };
        var resolver = Create(
            new RequestSecretReferenceOptions { AllowedEnvironmentVariables = ["IMPORT_TOKEN"] },
            inner);

        var failed = await FluentActions.Awaiting(() => resolver.ResolveAsync("env:IMPORT_TOKEN"))
            .Should().ThrowAsync<RequestSecretReferenceException>();
        var refused = await FluentActions.Awaiting(() => resolver.ResolveAsync("env:NOT_LISTED"))
            .Should().ThrowAsync<RequestSecretReferenceException>();

        failed.Which.Message.Should().Be(refused.Which.Message);
        failed.Which.Message.Should().NotContain("IMPORT_TOKEN");
        failed.Which.InnerException.Should().BeNull();
    }

    [UnitTest]
    public async Task ResolveAsync_WhenProviderReturnsBlank_Throws()
    {
        var resolver = Create(
            new RequestSecretReferenceOptions { AllowedEnvironmentVariables = ["IMPORT_TOKEN"] },
            new RecordingSecretResolver { Value = " " });

        await FluentActions.Awaiting(() => resolver.ResolveAsync("env:IMPORT_TOKEN"))
            .Should().ThrowAsync<RequestSecretReferenceException>();
    }

    [UnitTest]
    public void Validate_ReportsEntriesThatCanNeverMatch()
    {
        var options = new RequestSecretReferenceOptions
        {
            AllowedEnvironmentVariables = ["IMPORT_TOKEN", "NOT VALID"],
            AllowedEnvironmentVariablePrefixes = ["IMPORT_", "env:IMPORT_"],
            AllowedSecretReferencePrefixes = ["aws:secretsmanager:honua/", "aws:", "env:IMPORT_TOKEN", "honua/imports"]
        };

        options.Validate().Should().HaveCount(5);
        new RequestSecretReferenceOptions().Validate().Should().BeEmpty();
    }

    [UnitTest]
    public void BindOptions_ReadsListsFromTheSecuritySection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:RequestSecretReferences:AllowedEnvironmentVariables:0"] = "IMPORT_TOKEN",
                ["Security:RequestSecretReferences:AllowedEnvironmentVariablePrefixes:0"] = " IMPORT_ ",
                ["Security:RequestSecretReferences:AllowedSecretReferencePrefixes:0"] = "aws:secretsmanager:honua/imports/",
                ["Security:RequestSecretReferences:AllowedSecretReferencePrefixes:1"] = " "
            })
            .Build();

        var options = RequestSecretReferenceServiceCollectionExtensions.BindOptions(configuration);

        options.AllowedEnvironmentVariables.Should().Equal("IMPORT_TOKEN");
        options.AllowedEnvironmentVariablePrefixes.Should().Equal("IMPORT_");
        options.AllowedSecretReferencePrefixes.Should().Equal("aws:secretsmanager:honua/imports/");
        options.HasEntries.Should().BeTrue();
        RequestSecretReferenceServiceCollectionExtensions
            .BindOptions(new ConfigurationBuilder().Build())
            .HasEntries.Should().BeFalse();
    }

    private static RequestSecretReferenceResolver Create(
        RequestSecretReferenceOptions options,
        RecordingSecretResolver? inner = null)
        => new(inner ?? new RecordingSecretResolver(), options, NullLogger<RequestSecretReferenceResolver>.Instance);

    private sealed class RecordingSecretResolver : IConnectionSecretResolver
    {
        public List<string> Requested { get; } = [];

        public string? Value { get; init; } = SecretValue;

        public Exception? Failure { get; init; }

        public string ProviderName => "composite";

        public Task<string?> ResolveSecretAsync(string secretKey, CancellationToken cancellationToken = default)
        {
            Requested.Add(secretKey);
            return Failure is null ? Task.FromResult(Value) : Task.FromException<string?>(Failure);
        }

        public bool CanResolve(string secretKey) => true;

        public Task<string> ResolveConnectionStringAsync(string connectionStringTemplate, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Request-supplied references must not use template resolution.");

        public string[] GetSupportedProviders() => ["env", "aws", "azure", "null"];
    }
}
