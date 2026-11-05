// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Grounding.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Ai.Grounding;

internal static class GroundingServiceCollectionExtensions
{
    public static IServiceCollection AddGroundingServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<GroundingOptions>()
            .Bind(configuration.GetSection(GroundingOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<GroundingOptions>, GroundingOptionsValidator>());

        services.TryAddSingleton<IGroundingEngine, DeterministicGroundingEngine>();
        services.TryAddSingleton<IGroundingAuthorizationFilter, OperatorGroundingAuthorizationFilter>();
        services.TryAddSingleton<IGroundingService, GroundingService>();

        return services;
    }
}
