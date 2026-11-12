// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Assets;

internal static class AssetsServiceCollectionExtensions
{
    public static IServiceCollection AddAssets(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<AssetFormLookup>();
        services.AddScoped<AssetProfileValidator>();
        services.AddScoped<AssetProfileService>();
        return services;
    }
}
