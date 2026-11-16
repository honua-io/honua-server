// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Studio.Drafts;

public static class StudioDraftServiceCollectionExtensions
{
    public static IServiceCollection AddStudioDraftFactories(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDraftIdentifierGenerator, GuidDraftIdentifierGenerator>();
        services.TryAddSingleton<MapPackageDraftFactory>();
        services.TryAddSingleton<AppPackageDraftFactory>();

        // The store is what makes the identifier the factories mint actually
        // resolvable at honua://map-packages/{id} (honua-server#3262). It is a
        // constructor dependency of both tools and both package resources for the
        // same reason the factories are: a missing registration must fail at
        // startup, not degrade into a URI that silently never resolves.
        // In-memory is the store of a host with no system database. TryAdd keeps a
        // store the host already set.
        services.TryAddSingleton(new PackageDraftRetentionOptions());
        services.TryAddSingleton<IPackageDraftStore, InMemoryPackageDraftStore>();

        return services;
    }
}
