// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.AI;

namespace Honua.Infrastructure.WorkflowGeneration;

/// <summary>
/// Cloud-neutral seam for building an <see cref="IChatClient"/> targeting Azure OpenAI for the
/// studio generation flows.
/// </summary>
/// <remarks>
/// <para>
/// This interface lives in <c>Honua.Hosting</c> — the only assembly that BOTH <c>Honua.Ai</c>
/// (which hosts the cloud-neutral <c>AzureOpenAiWorkflowGenerationProvider</c>, reusing the
/// Ai-internal workflow schema/prompt/mapper) and <c>Honua.Azure</c> (which hosts the
/// <c>Azure.AI.OpenAI</c>-typed implementation) may reference under the module-dependency policy.
/// Keeping the SDK-typed adapter in <c>Honua.Azure</c> preserves the AWS/Azure SDK isolation
/// contract: no <c>Azure.*</c> type appears in this signature (only the cloud-neutral
/// <see cref="IChatClient"/> from <c>Microsoft.Extensions.AI</c>), so neither <c>Honua.Ai</c> nor
/// <c>Honua.Hosting</c> re-acquire the heavy Azure SDK surface.
/// </para>
/// <para>
/// The factory is registered by <c>Honua.Server</c> only when the Azure module is compiled in
/// (<c>HonuaIncludeAzure=true</c>); when it is absent the Azure OpenAI provider's
/// <c>IsConfigured</c> reports false and the provider is simply unselectable.
/// </para>
/// </remarks>
public interface IAzureOpenAiChatClientFactory
{
    /// <summary>
    /// Creates a chat client for the supplied Azure OpenAI <paramref name="deploymentName"/> on the
    /// resource at <paramref name="endpoint"/>. When <paramref name="apiKey"/> is null the Entra
    /// managed-identity credential chain (<c>DefaultAzureCredential</c>;
    /// <c>AZURE_CLIENT_ID</c> selects a user-assigned identity) is used; when supplied it is used as
    /// the Azure OpenAI key.
    /// </summary>
    /// <param name="endpoint">Azure OpenAI resource endpoint, e.g. <c>https://r.openai.azure.com</c>.</param>
    /// <param name="deploymentName">The Azure deployment name (carried as the model id).</param>
    /// <param name="apiVersion">Service API version, or null/empty for the SDK default.</param>
    /// <param name="apiKey">Optional API key; null prefers managed identity.</param>
    IChatClient Create(string endpoint, string deploymentName, string? apiVersion, string? apiKey);
}
