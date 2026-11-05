// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.StaticMap;

internal static class StaticMapServiceCollectionExtensions
{
    public static IServiceCollection AddStaticMap(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // StaticMap uses core services and MapServer rendering utilities.
        // No additional registrations needed.

        return services;
    }
}
