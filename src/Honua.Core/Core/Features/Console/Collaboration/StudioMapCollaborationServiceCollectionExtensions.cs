// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Console.Collaboration.Abstractions;
using Honua.Core.Features.Console.Collaboration.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Console.Collaboration;

public static class StudioMapCollaborationServiceCollectionExtensions
{
    public static IServiceCollection AddStudioMapCollaboration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IStudioMapCollaborationStore>(sp =>
            new InMemoryStudioMapCollaborationStore(sp.GetService<TimeProvider>()));
        return services;
    }
}
