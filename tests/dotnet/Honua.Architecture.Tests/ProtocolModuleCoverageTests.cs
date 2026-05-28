// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Server.Features.Infrastructure.Hosting;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Enforces that every protocol surface called out by the modularization plan
/// has a corresponding <see cref="IHonuaProtocolModule"/> implementation so the
/// future plugin host (Phase 1 follow-up) has something to discover.
/// </summary>
/// <remarks>
/// The test inspects the loaded <c>Honua.Server</c> assembly via reflection.
/// When a protocol is later extracted into its own assembly
/// (<c>Honua.Protocols.&lt;X&gt;</c>), the discovery surface widens to
/// include that assembly; the assertion stays the same.
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

    [ArchitectureTest]
    public void EveryRequiredProtocol_ShouldHaveAModuleImplementation()
    {
        var serverAssembly = typeof(IHonuaProtocolModule).Assembly;

        var moduleTypes = ArchitectureTestHelpers
            .GetTypesSafely(serverAssembly)
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .Where(t => typeof(IHonuaProtocolModule).IsAssignableFrom(t))
            .ToList();

        moduleTypes
            .Should()
            .NotBeEmpty("at least one IHonuaProtocolModule implementation must exist in Honua.Server");

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
        var serverAssembly = typeof(IHonuaProtocolModule).Assembly;

        var unsealedModules = ArchitectureTestHelpers
            .GetTypesSafely(serverAssembly)
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .Where(t => typeof(IHonuaProtocolModule).IsAssignableFrom(t))
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
        var serverAssembly = typeof(IHonuaProtocolModule).Assembly;

        var typesMissingParameterlessCtor = ArchitectureTestHelpers
            .GetTypesSafely(serverAssembly)
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .Where(t => typeof(IHonuaProtocolModule).IsAssignableFrom(t))
            .Where(t => t.GetConstructor(Type.EmptyTypes) is null)
            .Select(t => t.FullName)
            .ToList();

        typesMissingParameterlessCtor
            .Should()
            .BeEmpty("the host activates modules via reflection (Activator.CreateInstance) and cannot supply constructor arguments");
    }
}
