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
/// (Redshift: PA-181, Snowflake: PA-182). When a provider is explicitly enabled
/// via its legacy <c>X:Enabled=true</c> flag but the corresponding experimental
/// feature gate is not set, startup must throw <see cref="InvalidOperationException"/>
/// with a clear diagnostic pointing at the required flag. When neither the legacy
/// flag nor the experimental gate is set, startup must succeed silently.
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

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
