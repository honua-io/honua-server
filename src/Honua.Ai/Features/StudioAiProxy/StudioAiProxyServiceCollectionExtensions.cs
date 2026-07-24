// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.Providers.Bedrock;
using Honua.Ai.StudioAiProxy.Abstractions;
using Honua.Ai.StudioAiProxy.Adapters;
using Honua.Core.Features.Infrastructure.Resilience;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Ai.StudioAiProxy;

/// <summary>
/// DI registration for the Studio AI proxy (honua-server#3000). The orchestrating service and all
/// three adapters are always registered so the feature composes cleanly regardless of which
/// providers are configured; each adapter self-gates per provider via <c>IsConfigured</c>, and the
/// service as a whole self-gates via <see cref="StudioAiProxyConfiguration.Enabled"/>.
/// </summary>
internal static class StudioAiProxyServiceCollectionExtensions
{
    public static IServiceCollection AddStudioAiProxy(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(StudioAiProxyConfiguration.SectionName);

        services.AddOptions<StudioAiProxyConfiguration>()
            .Bind(section)
            .PostConfigure(options =>
            {
                // Per-provider API-key environment fallback (e.g. HONUA_STUDIOAI_MYPROVIDER_API_KEY),
                // mirroring WorkflowGeneration's HONUA_WORKFLOWGEN_* pattern.
                foreach (var (name, provider) in options.Providers)
                {
                    if (!string.IsNullOrWhiteSpace(provider.ApiKey))
                    {
                        continue;
                    }

                    var envKey = Environment.GetEnvironmentVariable(StudioAiProxyApiKeyResolver.EnvVarName(name));
                    if (!string.IsNullOrWhiteSpace(envKey))
                    {
                        provider.ApiKey = envKey;
                    }
                }
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<StudioAiProxyConfiguration>, StudioAiProxyConfigurationValidator>();

        services.AddResilientHttpClient(
            "studio-ai-proxy",
            "studio-ai-proxy",
            HttpResiliencePolicies.FastApiDefaults);
        services.TryAddSingleton<StudioAiProxyApiKeyResolver>();

        // Bedrock bridge: reuses the same chat-client factory the studio generation flows already
        // register against IBedrockChatClientFactory (TryAdd, so whichever feature initializes DI
        // first wins and the other is a no-op).
        services.TryAddSingleton<IBedrockChatClientFactory, BedrockChatClientFactory>();

        services.AddSingleton<IStudioAiProxyAdapter, AnthropicStudioAiProxyAdapter>();
        services.AddSingleton<IStudioAiProxyAdapter, OpenAiCompatibleStudioAiProxyAdapter>();
        services.AddSingleton<IStudioAiProxyAdapter, BedrockStudioAiProxyAdapter>();

        services.TryAddSingleton<IStudioAiProxyService, StudioAiProxyService>();
        return services;
    }
}
