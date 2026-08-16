// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.NlQuery;
using Honua.Core.Features.NlQuery.Abstractions;
using Honua.Ai.NlQuery;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.NlQuery;

/// <summary>
/// Registration and configuration coverage for the NlQuery plan-provider seam.
/// </summary>
/// <remarks>
/// The OpenAI-backed provider and its HTTP behaviour tests were removed with the
/// server-side generation families (ADR-0076): it was the last path on which the
/// server initiated model inference of its own accord. What survives is the seam
/// itself, the deterministic provider behind it, and the configuration rules that
/// now reject any provider other than <c>deterministic</c>.
/// </remarks>
[Protocol(TestProtocols.TestQuality)]
public sealed class NlQueryPlanProviderTests
{
    [UnitTest]
    [Operation(Operations.Query)]
    public void FeatureDisabled_ProviderNotRegistered()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["NlQuery:Enabled"] = "false"
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNlQuery(configuration);

        using var sp = services.BuildServiceProvider();

        sp.GetService<INlQueryPlanProvider>().Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void FeatureNotConfigured_ProviderNotRegistered()
    {
        // No NlQuery section at all.
        var configuration = BuildConfiguration([]);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNlQuery(configuration);

        using var sp = services.BuildServiceProvider();

        sp.GetService<INlQueryPlanProvider>().Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void FeatureEnabled_RegistersTheDeterministicProvider()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["NlQuery:Enabled"] = "true",
            ["NlQuery:Provider"] = "deterministic"
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNlQuery(configuration);

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        var provider = scope.ServiceProvider.GetService<INlQueryPlanProvider>();

        provider.Should().NotBeNull();
        provider.Should().BeOfType<DeterministicNlQueryPlanProvider>();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void FeatureEnabled_WithUnsupportedProvider_Throws()
    {
        // 'openai' is no longer a supported value: registering it would restore a
        // server-initiated inference path ADR-0076 removed.
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["NlQuery:Enabled"] = "true",
            ["NlQuery:Provider"] = "openai"
        });

        var services = new ServiceCollection();
        services.AddLogging();

        var act = () => services.AddNlQuery(configuration);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Unsupported NlQuery provider 'openai'*'deterministic'*");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ConfigurationValidator_WithUnsupportedProvider_FailsValidation()
    {
        var validator = new NlQueryConfigurationValidator();
        var options = new NlQueryConfiguration
        {
            Enabled = true,
            Provider = "openai"
        };

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(failure => failure.Contains("is not supported", StringComparison.Ordinal));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void FeatureEnabled_WithEnvironmentApiKey_BindsOptionsWithoutMutatingConfiguration()
    {
        const string envVariableName = "HONUA_NLQUERY_API_KEY";
        var previousValue = Environment.GetEnvironmentVariable(envVariableName);

        try
        {
            Environment.SetEnvironmentVariable(envVariableName, "env-key");

            var configuration = BuildConfiguration(new Dictionary<string, string?>
            {
                ["NlQuery:Enabled"] = "true",
                ["NlQuery:Provider"] = "deterministic"
            });

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNlQuery(configuration);

            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptions<NlQueryConfiguration>>().Value;

            options.ApiKey.Should().Be("env-key");
            configuration["NlQuery:ApiKey"].Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVariableName, previousValue);
        }
    }

    private static IConfigurationRoot BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
