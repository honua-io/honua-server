// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.AiBuilder.Fixtures;
using Honua.Ai.AiBuilder.Planning;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Ai.AiBuilder;

/// <summary>
/// DI registration for deterministic AI-builder fixture replay services.
/// </summary>
internal static class AiBuilderServiceCollectionExtensions
{
    /// <summary>
    /// Registers the embedded AI-builder fixture catalog used by deterministic
    /// planning and contract tests.
    /// </summary>
    public static IServiceCollection AddAiBuilderFixtures(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<AiBuilderFixtureCatalog>();
        return services;
    }

    /// <summary>
    /// Registers the default fixture-backed planner for AI-builder plan analysis.
    /// Hosts can replace <see cref="IPlanAnalysisService"/> after calling this
    /// method to wire a live planner implementation.
    /// </summary>
    public static IServiceCollection AddAiBuilderPlanAnalysis(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAiBuilderFixtures();
        services.TryAddSingleton<IPlanAnalysisService, FixturePlanAnalysisService>();
        return services;
    }
}
