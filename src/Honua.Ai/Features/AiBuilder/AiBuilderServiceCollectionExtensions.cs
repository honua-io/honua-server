// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Ai.AiBuilder.Fixtures;
using Honua.Ai.AiBuilder.Planning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Ai.AiBuilder;

internal static class AiBuilderServiceCollectionExtensions
{
    public static IServiceCollection AddAiBuilderFixtures(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<AiBuilderFixtureCatalog>();
        return services;
    }

    public static IServiceCollection AddAiBuilderPlanAnalysis(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAiBuilderFixtures();
        services.TryAddSingleton<IPlanAnalysisService, FixturePlanAnalysisService>();

        return services;
    }
}
