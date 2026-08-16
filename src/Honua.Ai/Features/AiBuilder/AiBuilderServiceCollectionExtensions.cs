// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.AiBuilder.Fixtures;
using Honua.Ai.AiBuilder.Planning;
using Microsoft.Extensions.DependencyInjection;
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
    /// Registers the deterministic fixture-backed planner behind the
    /// <c>honua_plan_analysis</c> MCP tool.
    /// </summary>
    /// <remarks>
    /// ADR-0076 (honua-server#3255) removed the live, provider-backed planner
    /// along with the <c>WorkflowGeneration</c> seam it rode on: the server no
    /// longer performs model inference of its own as part of executing a
    /// capability, so there is no longer a live lane to select between. The
    /// deterministic replay is the only planner, which is why this registration
    /// is unconditional rather than config-gated. Hosts can still call
    /// <c>services.Replace(...)</c> afterwards to substitute an implementation.
    /// </remarks>
    public static IServiceCollection AddAiBuilderPlanAnalysis(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAiBuilderFixtures();
        services.TryAddSingleton<IPlanAnalysisService, FixturePlanAnalysisService>();

        return services;
    }
}
