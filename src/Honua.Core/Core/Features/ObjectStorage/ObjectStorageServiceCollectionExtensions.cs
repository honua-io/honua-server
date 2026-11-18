// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.ObjectStorage;

public static class ObjectStorageServiceCollectionExtensions
{
    public static IServiceCollection AddObjectStorageConnections(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<ObjectStorageConnectionOptions>(configuration.GetSection(ObjectStorageConnectionOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IObjectStorageConnectionResolver, ObjectStorageConnectionResolver>();
        services.TryAddSingleton<ObjectStoreImportAvailability>();
        return services;
    }
}
