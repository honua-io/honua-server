// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Alerts;

/// <summary>
/// Configuration-binding tests for the operator-facing alert options: the worker enable switch
/// (<c>Alerts:Enabled</c>, #3055) and the downward-only alert-edition cap (<c>Alerts:Edition</c>,
/// #2998). Both are settable so the configuration binding source generator assigns them; these
/// tests bind the option the same way the host does
/// (<c>AddOptions().Bind(configuration.GetSection("Alerts"))</c>) so each configuration path is
/// covered independently of the HTTP surface.
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
    public void Bind_EnabledTrue_SetsEnabled()
        => Assert.True(Bind(("Alerts:Enabled", "true")).Enabled);

    [UnitTest]
    public void Bind_SettableProperties_AreBound()
    {
        var options = Bind(("Alerts:Enabled", "true"), ("Alerts:Edition", "Pro"));
        Assert.True(options.Enabled);
        Assert.Equal(AlertEdition.Pro, options.Edition);
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
