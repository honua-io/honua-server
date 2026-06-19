// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Honua.Infrastructure.WorkflowGeneration;
using Microsoft.Extensions.AI;

namespace Honua.Server.Features.WorkflowGeneration;

/// <summary>
/// Production <see cref="IAzureOpenAiChatClientFactory"/> backed by <c>Azure.AI.OpenAI</c>. Builds an
/// <see cref="AzureOpenAIClient"/> for the configured resource endpoint and wraps the deployment's
/// <c>ChatClient</c> as a cloud-neutral <see cref="IChatClient"/> via the <c>AsIChatClient()</c>
/// extension from <c>Microsoft.Extensions.AI.OpenAI</c>.
/// </summary>
/// <remarks>
/// All <c>Azure.*</c> SDK types are confined to this <c>Honua.Azure</c> assembly per the AWS/Azure
/// SDK isolation contract; the namespace stays <c>Honua.Server.Features.*</c> so the composition
/// root references it without naming any SDK type. Authentication prefers Entra managed identity
/// (<see cref="DefaultAzureCredential"/>; <c>AZURE_CLIENT_ID</c> selects a user-assigned identity);
/// an explicit key is used when supplied.
/// </remarks>
internal sealed class AzureOpenAiChatClientFactory : IAzureOpenAiChatClientFactory
{
    /// <inheritdoc />
    public IChatClient Create(string endpoint, string deploymentName, string? apiVersion, string? apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);

        var options = new AzureOpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(apiVersion)
            && TryParseServiceVersion(apiVersion, out var serviceVersion))
        {
            options = new AzureOpenAIClientOptions(serviceVersion);
        }

        var uri = new Uri(endpoint, UriKind.Absolute);

        // Key fallback when an explicit key is configured; otherwise the Entra managed-identity
        // credential chain. DefaultAzureCredential honors AZURE_CLIENT_ID for user-assigned MSI.
        var client = string.IsNullOrWhiteSpace(apiKey)
            ? new AzureOpenAIClient(uri, new DefaultAzureCredential(), options)
            : new AzureOpenAIClient(uri, new AzureKeyCredential(apiKey), options);

        return client.GetChatClient(deploymentName).AsIChatClient();
    }

    // Maps a service API version string (e.g. "2024-10-21") onto the SDK's ServiceVersion enum.
    // Unknown/empty versions fall back to the SDK default (handled by the caller).
    private static bool TryParseServiceVersion(string apiVersion, out AzureOpenAIClientOptions.ServiceVersion serviceVersion)
    {
        serviceVersion = default;
        var normalized = apiVersion.Trim();
        foreach (var candidate in Enum.GetValues<AzureOpenAIClientOptions.ServiceVersion>())
        {
            // Enum member names look like V2024_10_21; compare against the normalized API version.
            var memberName = candidate.ToString();
            var asVersion = memberName
                .TrimStart('V', 'v')
                .Replace('_', '-');
            if (string.Equals(asVersion, normalized, StringComparison.OrdinalIgnoreCase))
            {
                serviceVersion = candidate;
                return true;
            }
        }

        return false;
    }
}
