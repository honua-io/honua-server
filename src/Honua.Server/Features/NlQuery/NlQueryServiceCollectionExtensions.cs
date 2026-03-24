// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.NlQuery;
using Honua.Core.Features.NlQuery.Abstractions;
using Honua.Core.Features.NlQuery.Services;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.NlQuery;

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
        if (!string.IsNullOrWhiteSpace(provider) &&
            !string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported NlQuery provider '{provider}'. Only 'openai' is supported.");
        }

        // Resolve API key from environment variable if not set in config
        var apiKey = section.GetValue<string>("ApiKey");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var envKey = Environment.GetEnvironmentVariable("HONUA_NLQUERY_API_KEY");
            if (!string.IsNullOrWhiteSpace(envKey))
            {
                section["ApiKey"] = envKey;
            }
        }

        services.AddOptions<NlQueryConfiguration>()
            .Bind(section)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<NlQueryConfiguration>, NlQueryConfigurationValidator>();

        services.AddHttpClient("nl-query");
        services.AddScoped<INlQueryPlanProvider, OpenAiNlQueryPlanProvider>();
        return services;
    }
}
