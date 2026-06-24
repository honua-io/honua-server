// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.WorkflowGeneration;
using Honua.Core.Features.WorkflowPackages.Generation;

namespace Honua.Ai.Providers.AzureOpenAi;

/// <summary>
/// Resolved Azure OpenAI credentials: at most one of <see cref="ApiKey"/> /
/// <see cref="AccessToken"/> is non-empty, with the Entra (Managed-Identity) token preferred when
/// available, falling back to the api-key.
/// </summary>
public readonly record struct AzureOpenAiCredential(string ApiKey, string? AccessToken)
{
    /// <summary>Whether either an api-key or an Entra access token was resolved.</summary>
    public bool HasCredential => !string.IsNullOrWhiteSpace(ApiKey) || !string.IsNullOrWhiteSpace(AccessToken);
}

/// <summary>
/// Resolves Azure OpenAI authentication. Entra Managed Identity is the preferred auth (acquired via
/// <see cref="IAzureOpenAiTokenProvider"/>); when a Managed Identity is not available the resolver
/// falls back to a configured api-key — resolved through the shared
/// <see cref="WorkflowGenerationApiKeyResolver"/> so secret references and the per-provider
/// environment-variable fallback work identically to the other providers.
/// </summary>
public sealed class AzureOpenAiAuthResolver
{
    private readonly WorkflowGenerationApiKeyResolver _apiKeyResolver;
    private readonly IAzureOpenAiTokenProvider _tokenProvider;

    /// <summary>Creates an Azure OpenAI auth resolver over the shared key resolver and a token provider.</summary>
    public AzureOpenAiAuthResolver(
        WorkflowGenerationApiKeyResolver apiKeyResolver,
        IAzureOpenAiTokenProvider tokenProvider)
    {
        _apiKeyResolver = apiKeyResolver;
        _tokenProvider = tokenProvider;
    }

    /// <summary>
    /// Resolves a credential for the supplied provider. Prefers an Entra token; on absence falls
    /// back to the configured api-key (which may be a secret reference or env-var fallback).
    /// </summary>
    internal async Task<AzureOpenAiCredential> ResolveAsync(
        string providerId,
        WorkflowGenerationProviderOptions options,
        CancellationToken cancellationToken = default)
    {
        // Prefer Entra Managed Identity. Only attempt a token when no explicit api-key is
        // configured, so an operator who sets a key gets deterministic key auth.
        var apiKey = await _apiKeyResolver.ResolveAsync(providerId, options, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return new AzureOpenAiCredential(apiKey, AccessToken: null);
        }

        var token = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        return new AzureOpenAiCredential(ApiKey: string.Empty, AccessToken: token);
    }
}
