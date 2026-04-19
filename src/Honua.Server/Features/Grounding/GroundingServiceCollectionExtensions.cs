// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Grounding.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Grounding;

/// <summary>
/// DI registration for the grounding feature slice. All services are
/// stateless — the engine and authorization filter are compiled against frozen
/// catalogs and the evaluator respectively, so registering them as singletons
/// keeps the hot path allocation-free.
/// </summary>
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

        services.TryAddSingleton<IGroundingEngine, DeterministicGroundingEngine>();
        services.TryAddSingleton<IGroundingAuthorizationFilter, OperatorGroundingAuthorizationFilter>();
        services.TryAddSingleton<IGroundingService, GroundingService>();

        return services;
    }
}
