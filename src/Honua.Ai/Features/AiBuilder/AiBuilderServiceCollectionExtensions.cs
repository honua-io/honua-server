// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.AiBuilder.Fixtures;
using Honua.Ai.AiBuilder.Planning;
using Honua.Core.Features.WorkflowPackages.Generation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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

        // Bind the dedicated PlanAnalysis seam so the live planner can resolve its
        // provider override (PlanAnalysis:Provider) at request time. The fixture
        // path ignores it; binding is cheap and keeps a single registration site.
        // The validator only checks the provider override (against the same
        // allow-list as WorkflowGeneration) and is a no-op when no override is set.
        services.AddOptions<PlanAnalysisConfiguration>()
            .Bind(configuration.GetSection(PlanAnalysisConfiguration.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<PlanAnalysisConfiguration>, PlanAnalysisConfigurationValidator>());

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
    /// True when the live (provider-backed) plan lane should run instead of the
    /// deterministic fixture replay.
    /// </summary>
    /// <remarks>
    /// The dedicated <c>PlanAnalysis</c> seam (#1955) takes precedence over the
    /// Studio <c>WorkflowGeneration</c> gate so the MCP planner can be flipped
    /// independently:
    /// <list type="bullet">
    ///   <item>If <c>PlanAnalysis:Enabled</c> is explicitly set, it decides the
    ///   gate (true → live, false → fixtures) — the operator opts the MCP plan
    ///   lane in or out without touching the Studio generation feature.</item>
    ///   <item>Otherwise the planner inherits the <c>WorkflowGeneration</c> gate
    ///   (the original behaviour).</item>
    /// </list>
    /// In both cases the <em>effective provider</em> must be a live provider —
    /// <c>PlanAnalysis:Provider</c> when set, else
    /// <c>WorkflowGeneration:DefaultProvider</c> — and not the deterministic
    /// fixture-replay provider, otherwise the deterministic path is selected.
    /// </remarks>
    internal static bool ShouldUseLivePlanner(IConfiguration configuration)
    {
        var planSection = configuration.GetSection(PlanAnalysisConfiguration.SectionName);
        var workflowSection = configuration.GetSection(WorkflowGenerationConfiguration.SectionName);

        // PlanAnalysis:Enabled is authoritative when present; otherwise inherit
        // the WorkflowGeneration gate.
        var planEnabled = planSection.GetValue<bool?>("Enabled");
        var gateEnabled = planEnabled ?? workflowSection.GetValue<bool>("Enabled");
        if (!gateEnabled)
        {
            return false;
        }

        // PlanAnalysis:Provider overrides the WorkflowGeneration default provider
        // for the plan lane. A blank override falls back to the WorkflowGeneration
        // default so existing single-key deployments keep working.
        var effectiveProvider = ResolveEffectiveProvider(planSection, workflowSection);

        return effectiveProvider.Length > 0
            && !string.Equals(
                effectiveProvider,
                WorkflowGenerationConfiguration.DeterministicProviderId,
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the provider id the live planner will use: the
    /// <c>PlanAnalysis:Provider</c> override when non-blank, otherwise
    /// <c>WorkflowGeneration:DefaultProvider</c> (itself defaulting to the local
    /// provider id when unset).
    /// </summary>
    internal static string ResolveEffectiveProvider(
        IConfigurationSection planSection,
        IConfigurationSection workflowSection)
    {
        var planProvider = (planSection.GetValue<string>("Provider") ?? string.Empty).Trim();
        if (planProvider.Length > 0)
        {
            return planProvider;
        }

        return (workflowSection.GetValue<string>("DefaultProvider")
            ?? WorkflowGenerationConfiguration.LocalProviderId).Trim();
    }
}
