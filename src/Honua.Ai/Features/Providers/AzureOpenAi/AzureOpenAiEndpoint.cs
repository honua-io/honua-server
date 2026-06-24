// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Generation;

namespace Honua.Ai.Providers.AzureOpenAi;

/// <summary>
/// Builds the Azure OpenAI / Azure AI Foundry chat-completions request URL from the resource
/// endpoint, deployment name, and api-version. Azure OpenAI is OpenAI-API-compatible at the body
/// level but differs in routing: requests target
/// <c>{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={version}</c> rather
/// than the OpenAI <c>{endpoint}/chat/completions</c> shape, so the request body, response parsing,
/// and structured-output <c>json_schema</c> handling can all be shared with the OpenAI-compatible
/// path — only the URL and auth header differ.
/// </summary>
internal static class AzureOpenAiEndpoint
{
    /// <summary>
    /// Resolves the deployment name to route by: the explicit <see cref="WorkflowGenerationProviderOptions.Deployment"/>
    /// when set, otherwise the model id (Azure deployments are commonly named after the model).
    /// </summary>
    internal static string ResolveDeployment(WorkflowGenerationProviderOptions options, string model)
        => string.IsNullOrWhiteSpace(options.Deployment) ? model : options.Deployment;

    /// <summary>Resolves the api-version query value, falling back to the supported default.</summary>
    internal static string ResolveApiVersion(WorkflowGenerationProviderOptions options)
        => string.IsNullOrWhiteSpace(options.ApiVersion)
            ? WorkflowGenerationConfiguration.DefaultAzureOpenAiApiVersion
            : options.ApiVersion;

    /// <summary>
    /// Builds the absolute chat-completions URL for the supplied resource endpoint, deployment, and
    /// api-version.
    /// </summary>
    internal static string BuildChatCompletionsUri(string endpoint, string deployment, string apiVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(deployment);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiVersion);

        var baseUri = endpoint.TrimEnd('/');
        var encodedDeployment = Uri.EscapeDataString(deployment);
        var encodedVersion = Uri.EscapeDataString(apiVersion);
        return $"{baseUri}/openai/deployments/{encodedDeployment}/chat/completions?api-version={encodedVersion}";
    }
}
