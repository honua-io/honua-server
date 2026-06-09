// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;

namespace Honua.Plugins;

/// <summary>
/// Fluent builder used to register compile-time plugins with the host. Each
/// <see cref="Add{TPlugin}"/> call statically roots the plugin type (keeping discovery
/// AOT-safe — no assembly scanning), reads its <c>[Plugin]</c> manifest, and wires the
/// extension points it implements into DI.
/// </summary>
public interface IHonuaPluginBuilder
{
    /// <summary>
    /// Registers a plugin type. The type must be annotated with <c>[Plugin]</c> and implement
    /// at least one extension point (<c>IFeatureValidator</c> and/or <c>IEditHook</c>).
    /// </summary>
    /// <typeparam name="TPlugin">The concrete plugin type.</typeparam>
    /// <returns>The same builder for chaining.</returns>
    IHonuaPluginBuilder Add<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPlugin>()
        where TPlugin : class;
}
