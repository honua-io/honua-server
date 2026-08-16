// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.NlQuery;
using Honua.Core.Features.NlQuery.Abstractions;
using Honua.Ai.AiBuilder;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Ai.NlQuery;

/// <summary>
/// DI registration for the NL spatial query feature.
/// When the feature is disabled or not configured, nothing is registered.
/// </summary>
internal static class NlQueryServiceCollectionExtensions
{
    /// <summary>
    /// Registers NL query services when the feature is enabled in configuration.
    /// </summary>
    public static IServiceCollection AddNlQuery(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(NlQueryConfiguration.SectionName);
        if (!section.Exists() || !section.GetValue<bool>("Enabled"))
        {
            return services;
        }

        // The 'openai' provider was removed with the server-side generation families
        // (ADR-0076): it was the last path on which the server initiated model inference
        // of its own accord. Planning a FilterPlan from natural language is the client's
        // job; the server validates and compiles the plan it is handed.
        var provider = section.GetValue<string>("Provider");
        if (!string.IsNullOrWhiteSpace(provider)
            && !string.Equals(provider, "deterministic", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported NlQuery provider '{provider}'. Supported values: 'deterministic'. "
                + "Server-side model inference was removed in ADR-0076; supply a FilterPlan from the client instead.");
        }

        services.AddOptions<NlQueryConfiguration>()
            .Bind(section)
            .PostConfigure(options =>
            {
                if (string.IsNullOrWhiteSpace(options.ApiKey))
                {
                    var envKey = Environment.GetEnvironmentVariable("HONUA_NLQUERY_API_KEY");
                    if (!string.IsNullOrWhiteSpace(envKey))
                    {
                        options.ApiKey = envKey;
                    }
                }
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<NlQueryConfiguration>, NlQueryConfigurationValidator>();

        services.AddAiBuilderFixtures();
        services.AddScoped<INlQueryPlanProvider, DeterministicNlQueryPlanProvider>();

        services.AddScoped<INlQueryOrchestrator, NlQueryOrchestrator>();
        return services;
    }
}
