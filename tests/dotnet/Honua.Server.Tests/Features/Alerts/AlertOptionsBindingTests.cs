// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Alerts;

/// <summary>
/// Configuration-binding tests for the operator-facing alert worker switch and edition cap. These tests
/// bind the option the same way the host does
/// (<c>AddOptions().Bind(configuration.GetSection("Alerts"))</c>) so source-generated binding is covered
/// independently of the HTTP surface.
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

    [UnitTest]
    public void Bind_EnabledTrue_SetsWorkerSwitch()
        => Assert.True(Bind(("Alerts:Enabled", "true")).Enabled);

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
    public void Bind_EditionAbsent_LeavesCapNullSoTheEntitlementDecides()
        => Assert.Null(Bind(("Alerts:Enabled", "true")).Edition);

    [UnitTest]
    public void Bind_EditionEmpty_LeavesCapNullSoTheEntitlementDecides()
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
