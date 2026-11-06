// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Capabilities;

public static class CapabilityRegistryServiceCollectionExtensions
{
    public static IServiceCollection AddCapabilityRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ICapabilityRegistry, CapabilityRegistry>();
        return services;
    }

    public static IServiceCollection AddCapabilityFlagOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CapabilityFlagOptions>()
            .Configure(options =>
                CapabilityFlagOptions.Bind(options, configuration.GetSection(CapabilityFlagOptions.SectionName)));
        return services;
    }
}
