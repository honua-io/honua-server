// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Alerts;

/// <summary>
/// Configuration-binding tests for <see cref="AlertOptions.Edition"/> (#2998). The downward-only
/// alert-edition cap is operator-facing configuration (<c>Alerts:Edition</c> /
/// <c>Alerts__Edition</c>), and it is a nullable enum — a shape that silently binds to null when
/// it is not handled, which would drop the cap without any error. These tests bind the option the
/// same way the host does (<c>AddOptions().Bind(configuration.GetSection("Alerts"))</c>) so the
/// cap's configuration path is covered independently of the HTTP surface.
/// </summary>
public sealed class AlertOptionsBindingTests
{
    private static AlertOptions Bind(params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<AlertOptions>().Bind(configuration.GetSection(AlertOptions.SectionName));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AlertOptions>>().Value;
    }

    /// <summary>
    /// Characterizes a PRE-EXISTING gap that this change deliberately does not widen:
    /// <see cref="AlertOptions.Enabled"/> is init-only on trunk, and the configuration binding
    /// source generator does not assign init-only properties, so <c>Alerts:Enabled</c> does not
    /// bind. Asserted as-is so the day it starts working this test fails loudly and the
    /// follow-up can flip it. <see cref="AlertOptions.Edition"/> (this change's cap) is settable
    /// for exactly this reason.
    /// </summary>
    [UnitTest]
    public void Bind_EnabledInitOnly_DoesNotBind_PreExistingGap()
        => Assert.False(Bind(("Alerts:Enabled", "true")).Enabled);

    [UnitTest]
    public void Bind_SettableProperties_AreBound()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alerts:Enabled"] = "true",
                ["Alerts:Edition"] = "Pro",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<SettableAlertOptionsProbe>().Bind(configuration.GetSection(AlertOptions.SectionName));
        using var provider = services.BuildServiceProvider();
        var probe = provider.GetRequiredService<IOptions<SettableAlertOptionsProbe>>().Value;

        Assert.True(probe.Enabled);
        Assert.Equal(AlertEdition.Pro, probe.Edition);
    }

    [UnitTest]
    public void Bind_SectionExposesEditionValue()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Alerts:Edition"] = "Pro" })
            .Build();

        Assert.Equal("Pro", configuration.GetSection(AlertOptions.SectionName)["Edition"]);
    }

    [UnitTest]
    public void Bind_EditionPro_SetsDownwardCap()
        => Assert.Equal(AlertEdition.Pro, Bind(("Alerts:Edition", "Pro")).Edition);

    [UnitTest]
    public void Bind_EditionEnterprise_SetsDownwardCap()
        => Assert.Equal(AlertEdition.Enterprise, Bind(("Alerts:Edition", "Enterprise")).Edition);

    [UnitTest]
    public void Bind_EditionAbsent_LeavesCapNullSoTheLicenseDecides()
        => Assert.Null(Bind(("Alerts:Enabled", "true")).Edition);

    [UnitTest]
    public void Bind_EditionEmpty_LeavesCapNullSoTheLicenseDecides()
        => Assert.Null(Bind(("Alerts:Edition", string.Empty)).Edition);
}

/// <summary>
/// Control type for <see cref="AlertOptionsBindingTests.Bind_SettableProperties_AreBound"/>:
/// the same property shape as <see cref="AlertOptions"/> but with settable (not init-only)
/// accessors. Top-level and internal because the configuration binding source generator only
/// generates binding logic for public/internal types (SYSLIB1104).
/// </summary>
internal sealed class SettableAlertOptionsProbe
{
    public bool Enabled { get; set; }

    public AlertEdition? Edition { get; set; }
}
