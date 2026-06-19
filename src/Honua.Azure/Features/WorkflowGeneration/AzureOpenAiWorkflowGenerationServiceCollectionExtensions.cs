// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.WorkflowGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.WorkflowGeneration;

/// <summary>
/// Registers the Azure OpenAI <see cref="IAzureOpenAiChatClientFactory"/> implementation. Invoked by
/// the composition root only when the Azure module is compiled in (<c>HonuaIncludeAzure=true</c>).
/// The cloud-neutral <c>AzureOpenAiWorkflowGenerationProvider</c> (in <c>Honua.Ai</c>) consumes this
/// factory through the <c>Honua.Hosting</c> seam; when this registration is absent the provider's
/// <c>IsConfigured</c> reports false and it is simply unselectable.
/// </summary>
internal static class AzureOpenAiWorkflowGenerationServiceCollectionExtensions
{
    public static IServiceCollection AddAzureOpenAiChatClientFactory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Confined to Honua.Azure: the only Azure.AI.OpenAI-typed registration for the studio flows.
        services.TryAddSingleton<IAzureOpenAiChatClientFactory, AzureOpenAiChatClientFactory>();
        return services;
    }
}
