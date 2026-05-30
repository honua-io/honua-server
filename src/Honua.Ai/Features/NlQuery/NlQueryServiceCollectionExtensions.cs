// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.NlQuery;
using Honua.Core.Features.NlQuery.Abstractions;
using Honua.Core.Features.Infrastructure.Resilience;
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

        var provider = section.GetValue<string>("Provider");
        var isDeterministic = string.Equals(provider, "deterministic", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(provider)
            && !isDeterministic
            && !string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported NlQuery provider '{provider}'. Supported values: 'openai', 'deterministic'.");
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

        if (isDeterministic)
        {
            services.AddAiBuilderFixtures();
            services.AddScoped<INlQueryPlanProvider, DeterministicNlQueryPlanProvider>();
        }
        else
        {
            services.AddResilientHttpClient(
                "nl-query",
                "nl-query",
                HttpResiliencePolicies.FastApiDefaults);
            services.AddScoped<INlQueryPlanProvider, OpenAiNlQueryPlanProvider>();
        }

        services.AddScoped<INlQueryOrchestrator, NlQueryOrchestrator>();
        return services;
    }
}
