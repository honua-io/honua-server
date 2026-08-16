// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Studio.Drafts;

/// <summary>
/// Dependency injection helpers for the deterministic package draft factories
/// (ADR-0076).
/// </summary>
public static class StudioDraftServiceCollectionExtensions
{
    /// <summary>
    /// Registers the deterministic map and app draft factories and their clock
    /// and identifier dependencies.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <remarks>
    /// Every registration is a <c>TryAdd</c> singleton, so calling this more than
    /// once (the MCP surface composes it for self-sufficiency, and the server
    /// host composes it alongside the rest of the Studio slice) is idempotent.
    /// The factories are stateless and therefore safe as singletons, which is
    /// what lets the singleton MCP tools take them as constructor dependencies
    /// instead of resolving them through a nullable service locator — the
    /// failure mode ADR-0076 names explicitly, where a half-finished deletion
    /// compiles and the tools silently return an unavailable stub forever.
    /// </remarks>
    public static IServiceCollection AddStudioDraftFactories(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDraftIdentifierGenerator, GuidDraftIdentifierGenerator>();
        services.TryAddSingleton<IMapPackageDraftFactory, MapPackageDraftFactory>();
        services.TryAddSingleton<IAppPackageDraftFactory, AppPackageDraftFactory>();

        // The store is what makes the identifier the factories mint actually
        // resolvable at honua://map-packages/{id} (honua-server#3262). It is a
        // constructor dependency of both tools and both package resources for the
        // same reason the factories are: a missing registration must fail at
        // startup, not degrade into a URI that silently never resolves.
        services.TryAddSingleton(new PackageDraftRetentionOptions());
        services.TryAddSingleton<IPackageDraftStore, InMemoryPackageDraftStore>();

        return services;
    }
}
