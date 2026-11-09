// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Filtering;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Protocols.Stac;

internal static class StacServiceCollectionExtensions
{
    public static IServiceCollection AddStac(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<Cql2FilterProcessor>();
        services.AddScoped<StacSearchDependencies>();
        services.AddOptions<StacOptions>().BindConfiguration(StacOptions.SectionName);

        return services;
    }
}
