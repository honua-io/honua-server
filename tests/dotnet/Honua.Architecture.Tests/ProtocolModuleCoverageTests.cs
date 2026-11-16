// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Infrastructure.Hosting;
using Honua.Infrastructure.Hosting.Modules;
using Xunit;

namespace Honua.Architecture.Tests;

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
