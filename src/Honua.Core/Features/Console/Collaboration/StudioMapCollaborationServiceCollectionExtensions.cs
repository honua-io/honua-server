// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Console.Collaboration.Abstractions;
using Honua.Core.Features.Console.Collaboration.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Console.Collaboration;

/// <summary>
/// Dependency injection helpers for the durable Studio map collaboration slice
/// (honua-server#1278): comment threads + activity feed.
/// </summary>
public static class StudioMapCollaborationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory collaboration store as the default. A Postgres-backed
    /// store overrides it when Postgres persistence is active.
    /// </summary>
    public static IServiceCollection AddStudioMapCollaboration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IStudioMapCollaborationStore>(sp =>
            new InMemoryStudioMapCollaborationStore(sp.GetService<TimeProvider>()));
        return services;
    }
}
