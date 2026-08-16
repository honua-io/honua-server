// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.AI;

namespace Honua.Ai.StudioAiProxy.Adapters.Bedrock;

/// <summary>
/// Builds an <see cref="IChatClient"/> targeting AWS Bedrock for the Studio AI proxy's
/// <c>bedrock</c> provider kind. Abstracted so the adapter can be tested against a fake chat client
/// without an AWS account, while production resolves the real Converse-API-backed
/// <see cref="BedrockChatClientAdapter"/>.
/// </summary>
/// <remarks>
/// Internal: the only reason this was public was that the retired Dashboard and Report
/// generation services took it as a public constructor parameter from outside this feature
/// (ADR-0076, honua-server#3255). With those gone the seam is consumed solely by
/// <c>BedrockStudioAiProxyAdapter</c> inside this assembly and by the proxy's own tests
/// through <c>InternalsVisibleTo</c>.
/// </remarks>
internal interface IBedrockChatClientFactory
{
    /// <summary>
    /// Creates a chat client for the supplied model and region. <paramref name="apiKey"/> is an
    /// optional Bedrock bearer token; when null the AWS credential chain (IAM) is used.
    /// </summary>
    IChatClient Create(string model, string region, string? apiKey);
}

/// <summary>
/// Production <see cref="IBedrockChatClientFactory"/> backed by <see cref="BedrockChatClientAdapter"/>.
/// </summary>
internal sealed class BedrockChatClientFactory : IBedrockChatClientFactory
{
    /// <inheritdoc />
    public IChatClient Create(string model, string region, string? apiKey)
        => BedrockChatClientAdapter.Create(model, region, apiKey);
}
