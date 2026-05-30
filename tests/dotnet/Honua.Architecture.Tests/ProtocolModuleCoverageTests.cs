// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Server.Features.Infrastructure.Hosting;
using Honua.Server.Features.Infrastructure.Hosting.Modules;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Enforces that every protocol surface called out by the modularization plan
/// has a corresponding <see cref="IHonuaProtocolModule"/> implementation so the
/// plugin host has something to discover.
/// </summary>
/// <remarks>
/// The <see cref="IHonuaProtocolModule"/> contract lives in <c>Honua.Hosting</c>
/// after the audit-A1 relocation, but the concrete modules still ship in
/// <c>Honua.Server</c> (and, as protocols are physically extracted, in their
/// own <c>Honua.Protocols.&lt;X&gt;</c> assemblies). The discovery surface is
/// therefore the Server assembly unioned with every loaded
/// <c>Honua.Protocols.*</c> assembly — anchoring on the interface's own
/// assembly would scan Hosting, which holds the contract but no
/// implementations.
/// </remarks>
[Trait("Category", "Architecture")]
public sealed class ProtocolModuleCoverageTests
{
    private static readonly string[] RequiredModuleNames =
    {
        "OData",
        "OgcApi",
        "OgcClassic",
        "GeoServices",
    };

    /// <summary>
    /// Assemblies that may host protocol-module implementations: the Server
    /// composition root (anchored on a known module type to force-load it)
    /// plus any extracted <c>Honua.Protocols.*</c> assemblies already loaded
    /// into the AppDomain. Server references every protocol module, so loading
    /// the Server assembly transitively loads the extracted ones.
    /// </summary>
    private static List<Assembly> DiscoveryAssemblies()
    {
        var server = typeof(ODataProtocolModule).Assembly;
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Honua.Protocols.", StringComparison.Ordinal) == true)
            .Append(server)
            .Distinct()
            .ToList();
    }

    private static List<Type> DiscoverModuleTypes() => DiscoveryAssemblies()
        .SelectMany(ArchitectureTestHelpers.GetTypesSafely)
        .Where(t => t is { IsAbstract: false, IsInterface: false })
        .Where(t => typeof(IHonuaProtocolModule).IsAssignableFrom(t))
        .ToList();

    [ArchitectureTest]
    public void EveryRequiredProtocol_ShouldHaveAModuleImplementation()
    {
        var moduleTypes = DiscoverModuleTypes();

        moduleTypes
            .Should()
            .NotBeEmpty("at least one IHonuaProtocolModule implementation must exist in Honua.Server or an extracted Honua.Protocols.* assembly");

        var discoveredNames = moduleTypes
            .Select(t => (IHonuaProtocolModule)Activator.CreateInstance(t)!)
            .Select(m => m.Name)
            .ToList();

        foreach (var required in RequiredModuleNames)
        {
            discoveredNames
                .Should()
                .Contain(required, "every Phase 1 protocol must export an IHonuaProtocolModule");
        }
    }

    [ArchitectureTest]
    public void EveryProtocolModule_ShouldBeSealed()
    {
        var unsealedModules = DiscoverModuleTypes()
            .Where(t => !t.IsSealed)
            .Select(t => t.FullName)
            .ToList();

        unsealedModules
            .Should()
            .BeEmpty("protocol modules are leaf types — subclassing them would break the one-impl-per-protocol contract");
    }

    [ArchitectureTest]
    public void EveryProtocolModule_ShouldHaveAParameterlessConstructor()
    {
        var typesMissingParameterlessCtor = DiscoverModuleTypes()
            .Where(t => t.GetConstructor(Type.EmptyTypes) is null)
            .Select(t => t.FullName)
            .ToList();

        typesMissingParameterlessCtor
            .Should()
            .BeEmpty("the host activates modules via reflection (Activator.CreateInstance) and cannot supply constructor arguments");
    }
}
