// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.AiBuilder.Fixtures;
using Honua.Ai.AiBuilder.Planning;
using Honua.Core.Features.WorkflowPackages.Generation;
using Microsoft.Extensions.Configuration;
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
    /// Registers the planner backing the <c>honua_plan_analysis</c> MCP tool.
    /// Provider selection is config-driven and mirrors the
    /// <c>WorkflowGeneration</c> provider-selection pattern: when AI workflow
    /// generation is enabled (<c>WorkflowGeneration:Enabled=true</c>) with a live
    /// (non-deterministic) default provider — for example AWS Bedrock — the live
    /// <see cref="LivePlanAnalysisService"/> compiles arbitrary intents into
    /// executable plans through the shared Bedrock seam. Otherwise the
    /// deterministic <see cref="FixturePlanAnalysisService"/> remains in place so
    /// CI and replay stay AI-credit-free. The fixture catalog is always
    /// registered so the fallback path is available regardless.
    /// </summary>
    /// <remarks>
    /// The live planner depends on the WorkflowGeneration services
    /// (<c>IWorkflowGenerationService</c> / <c>IWorkflowNodeRegistry</c>); the
    /// server composition registers those via <c>AddWorkflowGeneration</c>. Hosts
    /// can still call <c>services.Replace(...)</c> after this method to force a
    /// specific implementation.
    /// </remarks>
    public static IServiceCollection AddAiBuilderPlanAnalysis(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddAiBuilderFixtures();

        if (ShouldUseLivePlanner(configuration))
        {
            services.TryAddSingleton<IPlanAnalysisService, LivePlanAnalysisService>();
        }
        else
        {
            services.TryAddSingleton<IPlanAnalysisService, FixturePlanAnalysisService>();
        }

        return services;
    }

    /// <summary>
    /// Registers the deterministic fixture-backed planner unconditionally. Kept
    /// for tests and hosts that have no AI provider plumbing wired.
    /// </summary>
    public static IServiceCollection AddAiBuilderFixturePlanAnalysis(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAiBuilderFixtures();
        services.TryAddSingleton<IPlanAnalysisService, FixturePlanAnalysisService>();
        return services;
    }

    /// <summary>
    /// True when an AI workflow-generation provider is configured such that the
    /// live plan lane should run instead of fixture replay: the feature is
    /// enabled and the default provider id is a live provider (anything other
    /// than the deterministic fixture provider).
    /// </summary>
    internal static bool ShouldUseLivePlanner(IConfiguration configuration)
    {
        var section = configuration.GetSection(WorkflowGenerationConfiguration.SectionName);
        if (!section.GetValue<bool>("Enabled"))
        {
            return false;
        }

        var defaultProvider = (section.GetValue<string>("DefaultProvider")
            ?? WorkflowGenerationConfiguration.LocalProviderId).Trim();

        return defaultProvider.Length > 0
            && !string.Equals(
                defaultProvider,
                WorkflowGenerationConfiguration.DeterministicProviderId,
                StringComparison.OrdinalIgnoreCase);
    }
}
