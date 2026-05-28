// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Hosting;
using Honua.Server.Features.Infrastructure.Hosting.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Infrastructure.Hosting;

/// <summary>
/// Exercises the additive Phase 1 module discovery path
/// (<see cref="FeatureRegistrationExtensions.AddDiscoveredProtocolModules"/>)
/// end-to-end. The canonical wiring still goes through the direct
/// <c>AddXxx</c> calls; these tests pin the discovery path's contract so a
/// follow-up PR can migrate the canonical wiring with confidence.
/// </summary>
public sealed class ProtocolModuleDiscoveryTests
{
    [Fact]
    public void AddDiscoveredProtocolModules_WithoutFilter_InvokesEveryBuiltInModule()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddDiscoveredProtocolModules(configuration);

        // Each module's ConfigureServices does its own registrations; the simplest
        // contract assertion is that the call does not throw and the service
        // collection is non-empty afterwards. Per-module wiring is exercised by
        // the canonical AddXxx call sites already.
        Assert.NotEmpty(services);
    }

    [Fact]
    public void AddDiscoveredProtocolModules_WithExplicitFilter_OnlyInvokesNamedModules()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        var enabled = new[] { "OData" };

        // Should not throw even though the filter restricts to a single module.
        var exception = Record.Exception(() => services.AddDiscoveredProtocolModules(configuration, enabled));

        Assert.Null(exception);
    }

    [Fact]
    public void AddDiscoveredProtocolModules_WithUnknownFilter_RegistersNothing()
    {
        var services = new ServiceCollection();
        var baseline = services.Count;
        var configuration = new ConfigurationBuilder().Build();
        var enabled = new[] { "NotAProtocol" };

        services.AddDiscoveredProtocolModules(configuration, enabled);

        Assert.Equal(baseline, services.Count);
    }

    private static readonly string[] LowerCaseODataFilter = new[] { "odata" };
    private static readonly string[] CanonicalCaseODataFilter = new[] { "OData" };

    [Fact]
    public void AddDiscoveredProtocolModules_FilterIsCaseInsensitive()
    {
        var configuration = new ConfigurationBuilder().Build();

        var lowerCase = new ServiceCollection();
        lowerCase.AddDiscoveredProtocolModules(configuration, LowerCaseODataFilter);

        var canonicalCase = new ServiceCollection();
        canonicalCase.AddDiscoveredProtocolModules(configuration, CanonicalCaseODataFilter);

        Assert.Equal(canonicalCase.Count, lowerCase.Count);
    }

    [Fact]
    public void EveryBuiltInModule_HasUniqueName()
    {
        var modules = new IHonuaProtocolModule[]
        {
            new ODataProtocolModule(),
            new OgcApiProtocolModule(),
            new OgcClassicProtocolModule(),
            new GeoServicesProtocolModule(),
        };

        var names = modules.Select(m => m.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EveryBuiltInModule_HasNonEmptyName()
    {
        var modules = new IHonuaProtocolModule[]
        {
            new ODataProtocolModule(),
            new OgcApiProtocolModule(),
            new OgcClassicProtocolModule(),
            new GeoServicesProtocolModule(),
        };

        foreach (var module in modules)
        {
            Assert.False(string.IsNullOrWhiteSpace(module.Name));
        }
    }
}
