// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Azure.Core;
using Azure.Identity;

namespace Honua.Ai.Providers.AzureOpenAi;

/// <summary>
/// Acquires an Entra (Azure AD) access token for the Azure OpenAI / Cognitive Services data plane.
/// Abstracted so the provider can be unit-tested without a live Managed Identity, while production
/// resolves the real <see cref="DefaultAzureCredential"/>-backed implementation. Returning
/// <see langword="null"/> signals "no Entra credential available" so the caller can fall back to
/// an api-key (or surface an actionable error).
/// </summary>
public interface IAzureOpenAiTokenProvider
{
    /// <summary>
    /// Returns a bearer token for the Azure OpenAI data plane
    /// (scope <c>https://cognitiveservices.azure.com/.default</c>), or <see langword="null"/> when
    /// no Entra credential is available in the current environment.
    /// </summary>
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Production <see cref="IAzureOpenAiTokenProvider"/> backed by <see cref="DefaultAzureCredential"/>
/// (Managed Identity in Azure-hosted environments; developer credential locally). This is the only
/// type that imports the Azure SDK for the AI provider, keeping the SDK surface confined the same
/// way the Bedrock adapter confines the AWS SDK — Honua.Core / Honua.Hosting / Honua.Server stay
/// SDK-free (enforced by CloudSdkIsolationTests).
/// </summary>
internal sealed class DefaultAzureOpenAiTokenProvider : IAzureOpenAiTokenProvider
{
    /// <summary>The Azure OpenAI data-plane scope for Entra (AAD) token acquisition.</summary>
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];

    private readonly TokenCredential _credential;

    public DefaultAzureOpenAiTokenProvider()
        : this(new DefaultAzureCredential())
    {
    }

    internal DefaultAzureOpenAiTokenProvider(TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        _credential = credential;
    }

    /// <inheritdoc />
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var token = await _credential
                .GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken)
                .ConfigureAwait(false);
            return token.Token;
        }
        catch (CredentialUnavailableException)
        {
            // No Managed Identity / developer credential in this environment — let the caller fall
            // back to an api-key rather than failing the request outright.
            return null;
        }
        catch (AuthenticationFailedException)
        {
            return null;
        }
    }
}
