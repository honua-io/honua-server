// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Abstractions;
using Honua.Scene.Assets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Infrastructure.Scene;

public static class SceneAssetHydrationServiceCollectionExtensions
{
    public static IServiceCollection AddSceneAssetHydration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISceneAssetHydrator, SceneAssetHydrator>();
        return services;
    }
}
