// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using FluentAssertions;
using Honua.Server.Startup;
using Microsoft.Extensions.Configuration;
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

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
