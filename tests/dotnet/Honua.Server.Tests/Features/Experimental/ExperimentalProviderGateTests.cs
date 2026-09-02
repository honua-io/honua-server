// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using FluentAssertions;
using Honua.Server.Features.Capabilities;
using Honua.Server.Startup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Honua.Server.Tests.Features.Experimental;

/// <summary>
/// Unit tests for the fail-closed startup gates on experimental data providers
/// (Redshift: PA-181, Snowflake: PA-182, Databricks: #2436). When a provider is
/// explicitly enabled via its legacy <c>X:Enabled=true</c> flag but the corresponding
/// experimental feature gate is not set, startup must throw <see cref="InvalidOperationException"/>
/// with a clear diagnostic pointing at the required flag. When neither the legacy
/// flag nor the experimental gate is set, startup must succeed silently.
///
/// <para>
/// All three warehouse providers must gate identically. Databricks was un-gated
/// until #2436: an operator could enable it by configuring a host alone, while the
/// identical action on Redshift threw. <see cref="EveryWarehouseProviderGatesIdentically"/>
/// is the regression guard, so a fourth warehouse provider cannot be added with the
/// gate quietly omitted.
/// </para>
/// </summary>
[Trait("Category", "ExperimentalGate")]
public sealed class ExperimentalProviderGateTests
{
    [Fact]
    public void CapabilityManifestRegistration_ProvidesWarehouseDecisionsWithoutInfrastructureComposition()
    {
        var services = new ServiceCollection();

        services.AddCapabilityManifest(BuildConfiguration(new Dictionary<string, string?>()));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<WarehouseProviderDecisions>().InfrastructureCompositionApplied.Should().BeFalse();
    }

    [Fact]
    public void CapabilityManifestRegistration_ReusesInfrastructureWarehouseDecisions()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["DataSource:Provider"] = "postgis",
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test",
        });
        var services = new ServiceCollection();
        InfrastructureCompositionRoot.RegisterInfrastructureServices(services, configuration);
        var startupDecisions = services.Single(descriptor => descriptor.ServiceType == typeof(WarehouseProviderDecisions))
            .ImplementationInstance.Should().BeOfType<WarehouseProviderDecisions>().Subject;

        services.AddCapabilityManifest(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<WarehouseProviderDecisions>().Should().BeSameAs(startupDecisions);
        startupDecisions.InfrastructureCompositionApplied.Should().BeTrue();
    }

    [Theory]
    [InlineData("Redshift", "absent", "absent", false, false)]
    [InlineData("Redshift", "false", "true", true, false)]
    [InlineData("Redshift", "true", "false", false, false)]
    [InlineData("Redshift", "true", "absent", false, true)]
    [InlineData("Snowflake", "absent", "absent", false, false)]
    [InlineData("Snowflake", "false", "true", true, false)]
    [InlineData("Snowflake", "true", "false", false, false)]
    [InlineData("Snowflake", "true", "absent", false, true)]
    [InlineData("Databricks", "absent", "absent", false, false)]
    [InlineData("Databricks", "false", "true", true, false)]
    [InlineData("Databricks", "true", "false", false, false)]
    [InlineData("Databricks", "true", "absent", false, true)]
    public void WarehouseProviderStateMatrix_UsesOneDecision(
        string provider,
        string gate,
        string enabled,
        bool throws,
        bool registered)
    {
        var values = new Dictionary<string, string?>
        {
            ["DataSource:Provider"] = "postgis",
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test",
        };
        if (gate != "absent") values[$"Experimental:Features:{provider}Provider"] = gate;
        if (enabled != "absent") values[$"{provider}:Enabled"] = enabled;
        var services = new ServiceCollection();

        var act = () => InfrastructureCompositionRoot.RegisterInfrastructureServices(
            services,
            BuildConfiguration(values));

        if (throws)
        {
            act.Should().Throw<InvalidOperationException>();
            return;
        }

        act.Should().NotThrow();
        var decisions = services.Single(descriptor => descriptor.ServiceType == typeof(WarehouseProviderDecisions))
            .ImplementationInstance.Should().BeOfType<WarehouseProviderDecisions>().Subject;
        var decision = decisions.All.Single(item => item.ConfigurationSection == provider);
        decision.Enabled.Should().Be(registered);
        services.Any(descriptor =>
                IsProviderType(descriptor.ServiceType, provider) || IsProviderType(descriptor.ImplementationType, provider))
            .Should().Be(registered, "runtime registration must equal the canonical decision");
    }

    // ---- Redshift fail-closed ----

    [Fact]
    public void Redshift_ExplicitlyEnabledWithoutExperimentalFlag_ThrowsAtStartup()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Redshift:Enabled"] = "true",
            ["Experimental:Features:RedshiftProvider"] = "false",
        });

        var act = () => InfrastructureCompositionRoot.RegisterInfrastructureServices(
            new Microsoft.Extensions.DependencyInjection.ServiceCollection(),
            configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Redshift:Enabled*Experimental:Features:RedshiftProvider*",
                "startup must fail with a clear message pointing to the experimental gate");
    }

    [Fact]
    public void Redshift_ExplicitlyEnabledWithExperimentalFlag_DoesNotThrow()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Redshift:Enabled"] = "true",
            ["Experimental:Features:RedshiftProvider"] = "true",
            // Minimal required keys so the composition root does not throw on other checks.
            ["DataSource:Provider"] = "postgis",
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test",
        });

        // Should not throw — registration proceeds normally.
        var act = () => InfrastructureCompositionRoot.RegisterInfrastructureServices(
            new Microsoft.Extensions.DependencyInjection.ServiceCollection(),
            configuration);

        // We only care that the fail-closed gate does not trigger.
        // Other exceptions (e.g., provider-not-found) are acceptable.
        act.Should().NotThrow<InvalidOperationException>(
            because: "the experimental gate must not block when both flags are set");
    }

    [Fact]
    public void Redshift_NotExplicitlyEnabled_ExperimentalFlagOff_DoesNotThrow()
    {
        // Redshift:Enabled not set (defaults to absent). Experimental flag off.
        // Must NOT fail — the default was true before this gate was added, so
        // operators who didn't set either flag should see a silent skip.
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Experimental:Features:RedshiftProvider"] = "false",
            ["DataSource:Provider"] = "postgis",
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test",
        });

        var act = () => InfrastructureCompositionRoot.RegisterInfrastructureServices(
            new Microsoft.Extensions.DependencyInjection.ServiceCollection(),
            configuration);

        act.Should().NotThrow<InvalidOperationException>(
            because: "when Redshift:Enabled is not set, the fail-closed gate must not trigger");
    }

    // ---- Snowflake fail-closed ----

    [Fact]
    public void Snowflake_ExplicitlyEnabledWithoutExperimentalFlag_ThrowsAtStartup()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Snowflake:Enabled"] = "true",
            ["Experimental:Features:SnowflakeProvider"] = "false",
        });

        var act = () => InfrastructureCompositionRoot.RegisterInfrastructureServices(
            new Microsoft.Extensions.DependencyInjection.ServiceCollection(),
            configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Snowflake:Enabled*Experimental:Features:SnowflakeProvider*",
                "startup must fail with a clear message pointing to the experimental gate");
    }

    [Fact]
    public void Snowflake_NotExplicitlyEnabled_ExperimentalFlagOff_DoesNotThrow()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Experimental:Features:SnowflakeProvider"] = "false",
            ["DataSource:Provider"] = "postgis",
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test",
        });

        var act = () => InfrastructureCompositionRoot.RegisterInfrastructureServices(
            new Microsoft.Extensions.DependencyInjection.ServiceCollection(),
            configuration);

        act.Should().NotThrow<InvalidOperationException>(
            because: "when Snowflake:Enabled is not set, the fail-closed gate must not trigger");
    }

    // ---- Databricks fail-closed (#2436) ----

    [Fact]
    public void Databricks_ExplicitlyEnabledWithoutExperimentalFlag_ThrowsAtStartup()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Databricks:Enabled"] = "true",
            ["Experimental:Features:DatabricksProvider"] = "false",
        });

        var act = () => InfrastructureCompositionRoot.RegisterInfrastructureServices(
            new Microsoft.Extensions.DependencyInjection.ServiceCollection(),
            configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Databricks:Enabled*Experimental:Features:DatabricksProvider*",
                "startup must fail with a clear message pointing to the experimental gate");
    }

    [Fact]
    public void Databricks_NotExplicitlyEnabled_ExperimentalFlagOff_DoesNotThrow()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Experimental:Features:DatabricksProvider"] = "false",
            ["DataSource:Provider"] = "postgis",
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test",
        });

        var act = () => InfrastructureCompositionRoot.RegisterInfrastructureServices(
            new Microsoft.Extensions.DependencyInjection.ServiceCollection(),
            configuration);

        act.Should().NotThrow<InvalidOperationException>(
            because: "when Databricks:Enabled is not set, the fail-closed gate must not trigger");
    }

    [Fact]
    public void Databricks_HostConfiguredWithoutExperimentalFlag_DoesNotRegisterProvider()
    {
        // The specific hole #2436 closed. Configuring a host was previously enough to
        // register Databricks, because it had no gate to opt into — the provider was
        // dormant-until-configured rather than experimental-until-opted-in. Supplying a
        // host must now leave the provider unregistered while the gate is off.
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Databricks:Host"] = "https://dbc-test.cloud.databricks.com",
            ["Databricks:WarehouseId"] = "warehouse-1",
            ["Databricks:Token"] = "token",
            ["DataSource:Provider"] = "postgis",
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test",
        });

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        InfrastructureCompositionRoot.RegisterInfrastructureServices(services, configuration);

        // Asserted by namespace rather than by type: the provider's registry and feature
        // store are internal to Honua.Db, so the test cannot name them.
        services.Should().NotContain(
            descriptor => IsDatabricksType(descriptor.ServiceType) || IsDatabricksType(descriptor.ImplementationType),
            "a configured host must not enable an experimental provider on its own");
    }

    [Fact]
    public void EveryWarehouseProviderGatesIdentically()
    {
        // Regression guard for the asymmetry itself rather than for one provider.
        // Adding a fourth warehouse provider without its gate should fail here.
        foreach (var (provider, gate) in new[]
                 {
                     ("Redshift", "Experimental:Features:RedshiftProvider"),
                     ("Snowflake", "Experimental:Features:SnowflakeProvider"),
                     ("Databricks", "Experimental:Features:DatabricksProvider"),
                 })
        {
            var configuration = BuildConfiguration(new Dictionary<string, string?>
            {
                [$"{provider}:Enabled"] = "true",
                [gate] = "false",
            });

            var act = () => InfrastructureCompositionRoot.RegisterInfrastructureServices(
                new Microsoft.Extensions.DependencyInjection.ServiceCollection(),
                configuration);

            act.Should().Throw<InvalidOperationException>(
                    because: $"{provider} must fail closed without {gate}")
                .WithMessage($"*{provider}:Enabled*{gate}*");
        }
    }

    private static bool IsDatabricksType(Type? type) =>
        type?.FullName?.StartsWith("Honua.Db.Databricks.", StringComparison.Ordinal) == true;

    private static bool IsProviderType(Type? type, string provider) =>
        type?.FullName?.Contains(provider, StringComparison.OrdinalIgnoreCase) == true;

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
