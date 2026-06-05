// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.WorkflowPackages.Generation;
using Honua.Core.Features.WorkflowPackages.Generation.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Ai.WorkflowGeneration;

/// <summary>
/// DI registration for the natural-language workflow-generation feature. The orchestrating
/// service and the deterministic (fixture) provider are always registered so the feature can be
/// exercised end-to-end without a live model; the live OpenAI-compatible (<c>local</c>/<c>openai</c>)
/// and Anthropic (<c>claude</c>) providers self-gate on configuration via <c>IsConfigured</c>.
/// </summary>
internal static class WorkflowGenerationServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowGeneration(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(WorkflowGenerationConfiguration.SectionName);

        services.AddOptions<WorkflowGenerationConfiguration>()
            .Bind(section)
            .PostConfigure(options =>
            {
                // Per-provider API-key environment fallback (e.g. HONUA_WORKFLOWGEN_OPENAI_API_KEY),
                // mirroring NlQuery's HONUA_NLQUERY_API_KEY pattern.
                foreach (var (id, provider) in options.Providers)
                {
                    if (!string.IsNullOrWhiteSpace(provider.ApiKey))
                    {
                        continue;
                    }

                    var envKey = Environment.GetEnvironmentVariable(
                        $"HONUA_WORKFLOWGEN_{id.ToUpperInvariant()}_API_KEY");
                    if (!string.IsNullOrWhiteSpace(envKey))
                    {
                        provider.ApiKey = envKey;
                    }
                }
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<WorkflowGenerationConfiguration>, WorkflowGenerationConfigurationValidator>();

        // Deterministic fixture provider: always available (no network), the CI/eval mode.
        services.TryAddSingleton<WorkflowGenerationFixtureCatalog>();
        services.AddSingleton<IWorkflowGenerationProvider, DeterministicWorkflowGenerationProvider>();

        // Live providers: one OpenAI-compatible instance per hosted id ("local" + "openai") plus
        // Anthropic ("claude"). Each self-gates via IsConfigured against its config block, so they are
        // harmless to register unconditionally and only become selectable once configured.
        services.AddResilientHttpClient(
            "workflow-generation",
            "workflow-generation",
            HttpResiliencePolicies.FastApiDefaults);
        services.TryAddSingleton<WorkflowGenerationApiKeyResolver>();
        services.AddSingleton<IWorkflowGenerationProvider>(sp =>
            ActivatorUtilities.CreateInstance<OpenAiCompatibleWorkflowGenerationProvider>(
                sp, WorkflowGenerationConfiguration.LocalProviderId));
        services.AddSingleton<IWorkflowGenerationProvider>(sp =>
            ActivatorUtilities.CreateInstance<OpenAiCompatibleWorkflowGenerationProvider>(
                sp, WorkflowGenerationConfiguration.OpenAiProviderId));
        services.AddSingleton<IWorkflowGenerationProvider>(sp =>
            ActivatorUtilities.CreateInstance<AnthropicWorkflowGenerationProvider>(
                sp, WorkflowGenerationConfiguration.AnthropicProviderId));

        services.TryAddSingleton<IWorkflowGenerationService, WorkflowGenerationService>();
        return services;
    }
}
